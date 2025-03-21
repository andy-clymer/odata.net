﻿//---------------------------------------------------------------------
// <copyright file="ODataJsonInputContext.cs" company="Microsoft">
//      Copyright (C) Microsoft Corporation. All rights reserved. See License.txt in the project root for license information.
// </copyright>
//---------------------------------------------------------------------

namespace Microsoft.OData.Json
{
    #region Namespaces
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.OData.Edm;
    using Microsoft.OData.Metadata;
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Text;
    using System.Threading.Tasks;
    // ReSharper disable RedundantUsingDirective
    using Microsoft.OData.Core;

    // ReSharper restore RedundantUsingDirective
    #endregion Namespaces

    /// <summary>
    /// Implementation of the OData input for Json OData format.
    /// </summary>
    internal sealed class ODataJsonInputContext : ODataInputContext
    {
        /// <summary>
        /// The json metadata level (i.e., full, none, minimal) being written.
        /// </summary>
        private readonly JsonMetadataLevel metadataLevel;

        /// <summary>The text reader created for the input stream.</summary>
        /// <remarks>
        /// The ODataJsonInputContext instance owns the textReader instance and thus disposes it.
        /// We further set this field to null when the input is disposed and use it for checks whether the instance has already been disposed.
        /// </remarks>
        private TextReader textReader;

        /// <summary>The JSON reader to read from.</summary>
        private BufferingJsonReader jsonReader;

        /// <summary>
        /// The Json message stream.
        /// </summary>
        private Stream stream;

        /// <summary>Constructor.</summary>
        /// <param name="messageInfo">The context information for the message.</param>
        /// <param name="messageReaderSettings">Configuration settings of the OData reader.</param>
        public ODataJsonInputContext(
            ODataMessageInfo messageInfo,
            ODataMessageReaderSettings messageReaderSettings)
            : this(CreateTextReader(messageInfo.MessageStream, messageInfo.Encoding), messageInfo, messageReaderSettings)
        {
            Debug.Assert(messageInfo.MessageStream != null, "messageInfo.MessageStream != null");
            this.stream = messageInfo.MessageStream;
        }

        /// <summary>Constructor.</summary>
        /// <param name="textReader">The text reader to use.</param>
        /// <param name="messageInfo">The context information for the message.</param>
        /// <param name="messageReaderSettings">Configuration settings of the OData reader.</param>
        internal ODataJsonInputContext(
            TextReader textReader,
            ODataMessageInfo messageInfo,
            ODataMessageReaderSettings messageReaderSettings)
            : base(ODataFormat.Json, messageInfo, messageReaderSettings)
        {
            Debug.Assert(messageInfo.MediaType != null, "messageInfo.MediaType != null");

            try
            {
                this.textReader = textReader;
                var innerReader = CreateJsonReader(this.Container, this.textReader, messageInfo.MediaType.HasIeee754CompatibleSetToTrue());
                if (messageReaderSettings.ArrayPool != null)
                {
                    // make sure customer also can use reading setting if without DI.
                    JsonReader jsonReader = innerReader as JsonReader;
                    if (jsonReader != null && jsonReader.ArrayPool == null)
                    {
                        jsonReader.ArrayPool = messageReaderSettings.ArrayPool;
                    }
                }

                if (messageInfo.MediaType.HasStreamingSetToTrue())
                {
                    this.jsonReader = new BufferingJsonReader(
                        innerReader,
                        ODataJsonConstants.ODataErrorPropertyName,
                        messageReaderSettings.MessageQuotas.MaxNestingDepth);
                }
                else
                {
                    // If we have a non-streaming Json content type we need to use the re-ordering Json reader
                    this.jsonReader = new ReorderingJsonReader(innerReader, messageReaderSettings.MessageQuotas.MaxNestingDepth);
                }
            }
            catch (Exception e)
            {
                // Dispose the message stream if we failed to create the input context.
                if (ExceptionUtils.IsCatchableExceptionType(e) && this.textReader != null)
                {
                    this.textReader.Dispose();
                }

                throw;
            }

            // don't know how to get MetadataDocumentUri uri here, messageReaderSettings do not have one
            // Uri metadataDocumentUri = messageReaderSettings..MetadataDocumentUri == null ? null : messageReaderSettings.MetadataDocumentUri.BaseUri;
            // the uri here is used here to create the FullMetadataLevel can pass null in
            this.metadataLevel = JsonMetadataLevel.Create(messageInfo.MediaType, null, false, this.Model, this.ReadingResponse);
        }

        /// <summary>
        /// The json metadata level (i.e., full, none, minimal) being written.
        /// </summary>
        public JsonMetadataLevel MetadataLevel
        {
            get
            {
                return this.metadataLevel;
            }
        }

        /// <summary>
        /// Returns the <see cref="BufferingJsonReader"/> which is to be used to read the content of the message.
        /// </summary>
        public BufferingJsonReader JsonReader
        {
            get
            {
                Debug.Assert(this.jsonReader != null, "Trying to get JsonReader while none is available.");
                return this.jsonReader;
            }
        }

        /// <summary>
        /// The stream of the Json input context.
        /// </summary>
        internal Stream Stream
        {
            get
            {
                return this.stream;
            }
        }

        /// <summary>
        /// Returns whether to read odata control information without the odata prefix.
        /// True for OData 4.01 and greater. Settable for OData 4.0
        /// </summary>
        internal bool OptionalODataPrefix
        {
            get
            {
                if (this.MessageReaderSettings.Version == ODataVersion.V4)
                {
                    return this.MessageReaderSettings.EnableReadingODataAnnotationWithoutPrefix;
                }

                return true;
            }
        }

        /// <summary>
        /// Creates an <see cref="ODataReader" /> to read a resource set.
        /// </summary>
        /// <param name="entitySet">The entity set we are going to read resources for.</param>
        /// <param name="expectedResourceType">The expected structured type for the items in the resource set.</param>
        /// <returns>The newly created <see cref="ODataReader"/>.</returns>
        public override ODataReader CreateResourceSetReader(IEdmEntitySetBase entitySet, IEdmStructuredType expectedResourceType)
        {
            this.AssertSynchronous();
            this.VerifyCanCreateODataReader(entitySet, expectedResourceType);

            return this.CreateResourceSetReaderImplementation(entitySet, expectedResourceType, /*readingParameter*/ false, /*readingDelta*/ false);
        }

        /// <summary>
        /// Asynchronously creates an <see cref="ODataReader" /> to read a resource set.
        /// </summary>
        /// <param name="entitySet">The entity set we are going to read resources for.</param>
        /// <param name="expectedResourceType">The expected structured type for the items in the resource set.</param>
        /// <returns>Task which when completed returns the newly created <see cref="ODataReader"/>.</returns>
        public override Task<ODataReader> CreateResourceSetReaderAsync(IEdmEntitySetBase entitySet, IEdmStructuredType expectedResourceType)
        {
            this.AssertAsynchronous();
            this.VerifyCanCreateODataReader(entitySet, expectedResourceType);

            // Note that the reading is actually synchronous since we buffer the entire input when getting the stream from the message.
            return TaskUtils.GetTaskForSynchronousOperation(() => this.CreateResourceSetReaderImplementation(entitySet, expectedResourceType, /*readingParameter*/ false, /*readingDelta*/ false));
        }

        /// <summary>
        /// Creates an <see cref="ODataReader" /> to read a delta resource set.
        /// </summary>
        /// <param name="entitySet">The entity set we are going to read resources for.</param>
        /// <param name="expectedResourceType">The expected structured type for the items in the resource set.</param>
        /// <returns>The newly created <see cref="ODataReader"/>.</returns>
        public override ODataReader CreateDeltaResourceSetReader(IEdmEntitySetBase entitySet, IEdmStructuredType expectedResourceType)
        {
            this.AssertSynchronous();
            this.VerifyCanCreateODataReader(entitySet, expectedResourceType);

            return this.CreateResourceSetReaderImplementation(entitySet, expectedResourceType, /*readingParameter*/ false, /*readingDelta*/ true);
        }

        /// <summary>
        /// Asynchronously creates an <see cref="ODataReader" /> to read a delta resource set.
        /// </summary>
        /// <param name="entitySet">The entity set we are going to read resources for.</param>
        /// <param name="expectedResourceType">The expected structured type for the items in the resource set.</param>
        /// <returns>Task which when completed returns the newly created <see cref="ODataReader"/>.</returns>
        public override Task<ODataReader> CreateDeltaResourceSetReaderAsync(IEdmEntitySetBase entitySet, IEdmStructuredType expectedResourceType)
        {
            this.AssertAsynchronous();
            this.VerifyCanCreateODataReader(entitySet, expectedResourceType);

            // Note that the reading is actually synchronous since we buffer the entire input when getting the stream from the message.
            return TaskUtils.GetTaskForSynchronousOperation(() => this.CreateResourceSetReaderImplementation(entitySet, expectedResourceType, /*readingParameter*/ false, /*readingDelta*/ true));
        }

        /// <summary>
        /// Creates an <see cref="ODataReader" /> to read a resource.
        /// </summary>
        /// <param name="navigationSource">The navigation source we are going to read resources for.</param>
        /// <param name="expectedResourceType">The expected resource type for the resource to be read.</param>
        /// <returns>The newly created <see cref="ODataReader"/>.</returns>
        public override ODataReader CreateResourceReader(IEdmNavigationSource navigationSource, IEdmStructuredType expectedResourceType)
        {
            this.AssertSynchronous();
            this.VerifyCanCreateODataReader(navigationSource, expectedResourceType);

            return this.CreateResourceReaderImplementation(navigationSource, expectedResourceType);
        }

        /// <summary>
        /// Asynchronously creates an <see cref="ODataReader" /> to read a resource.
        /// </summary>
        /// <param name="navigationSource">The navigation source we are going to read resources for.</param>
        /// <param name="expectedResourceType">The expected structured type for the resource to be read.</param>
        /// <returns>Task which when completed returns the newly created <see cref="ODataReader"/>.</returns>
        public override Task<ODataReader> CreateResourceReaderAsync(IEdmNavigationSource navigationSource, IEdmStructuredType expectedResourceType)
        {
            this.AssertAsynchronous();
            this.VerifyCanCreateODataReader(navigationSource, expectedResourceType);

            // Note that the reading is actually synchronous since we buffer the entire input when getting the stream from the message.
            return TaskUtils.GetTaskForSynchronousOperation(() => this.CreateResourceReaderImplementation(navigationSource, expectedResourceType));
        }

        /// <summary>
        /// Create a <see cref="ODataCollectionReader"/>.
        /// </summary>
        /// <param name="expectedItemTypeReference">The expected type reference for the items in the collection.</param>
        /// <returns>Newly create <see cref="ODataCollectionReader"/>.</returns>
        public override ODataCollectionReader CreateCollectionReader(IEdmTypeReference expectedItemTypeReference)
        {
            this.AssertSynchronous();
            this.VerifyCanCreateCollectionReader(expectedItemTypeReference);

            return this.CreateCollectionReaderImplementation(expectedItemTypeReference);
        }

        /// <summary>
        /// Asynchronously create a <see cref="ODataCollectionReader"/>.
        /// </summary>
        /// <param name="expectedItemTypeReference">The expected type reference for the items in the collection.</param>
        /// <returns>Task which when completed returns the newly create <see cref="ODataCollectionReader"/>.</returns>
        public override Task<ODataCollectionReader> CreateCollectionReaderAsync(IEdmTypeReference expectedItemTypeReference)
        {
            this.AssertAsynchronous();
            this.VerifyCanCreateCollectionReader(expectedItemTypeReference);

            // Note that the reading is actually synchronous since we buffer the entire input when getting the stream from the message.
            return TaskUtils.GetTaskForSynchronousOperation(() => this.CreateCollectionReaderImplementation(expectedItemTypeReference));
        }

        /// <summary>
        /// This method creates an reads the property from the input and
        /// returns an <see cref="ODataProperty"/> representing the read property.
        /// </summary>
        /// <param name="property">The <see cref="IEdmProperty"/> producing the property to be read.</param>
        /// <param name="expectedPropertyTypeReference">The expected type reference of the property to read.</param>
        /// <returns>An <see cref="ODataProperty"/> representing the read property.</returns>
        public override ODataProperty ReadProperty(IEdmStructuralProperty property, IEdmTypeReference expectedPropertyTypeReference)
        {
            this.AssertSynchronous();
            this.VerifyCanReadProperty();

            ODataJsonPropertyAndValueDeserializer jsonPropertyAndValueDeserializer = new ODataJsonPropertyAndValueDeserializer(this);
            return jsonPropertyAndValueDeserializer.ReadTopLevelProperty(expectedPropertyTypeReference);
        }

        /// <summary>
        /// Asynchronously read the property from the input and
        /// return an <see cref="ODataProperty"/> representing the read property.
        /// </summary>
        /// <param name="property">The <see cref="IEdmProperty"/> producing the property to be read.</param>
        /// <param name="expectedPropertyTypeReference">The expected type reference of the property to read.</param>
        /// <returns>Task which when completed returns an <see cref="ODataProperty"/> representing the read property.</returns>
        public override Task<ODataProperty> ReadPropertyAsync(IEdmStructuralProperty property, IEdmTypeReference expectedPropertyTypeReference)
        {
            this.AssertAsynchronous();
            this.VerifyCanReadProperty();

            ODataJsonPropertyAndValueDeserializer jsonPropertyAndValueDeserializer = new ODataJsonPropertyAndValueDeserializer(this);
            return jsonPropertyAndValueDeserializer.ReadTopLevelPropertyAsync(expectedPropertyTypeReference);
        }


        /// <summary>
        /// Read a top-level error.
        /// </summary>
        /// <returns>An <see cref="ODataError"/> representing the read error.</returns>
        public override ODataError ReadError()
        {
            this.AssertSynchronous();

            ODataJsonErrorDeserializer jsonErrorDeserializer = new ODataJsonErrorDeserializer(this);
            return jsonErrorDeserializer.ReadTopLevelError();
        }

        /// <summary>
        /// Asynchronously read a top-level error.
        /// </summary>
        /// <returns>Task which when completed returns an <see cref="ODataError"/> representing the read error.</returns>
        public override Task<ODataError> ReadErrorAsync()
        {
            this.AssertAsynchronous();

            ODataJsonErrorDeserializer jsonErrorDeserializer = new ODataJsonErrorDeserializer(this);
            return jsonErrorDeserializer.ReadTopLevelErrorAsync();
        }

        /// <summary>
        /// Creates an <see cref="ODataReader" /> to read a resource set in a Uri operation parameter.
        /// </summary>
        /// <param name="entitySet">The entity set we are going to read resources for.</param>
        /// <param name="expectedResourceType">The expected structured type for the items in the resource set.</param>
        /// <returns>The newly created <see cref="ODataReader"/>.</returns>
        public override ODataReader CreateUriParameterResourceSetReader(IEdmEntitySetBase entitySet, IEdmStructuredType expectedResourceType)
        {
            this.AssertSynchronous();
            this.VerifyCanCreateODataReader(entitySet, expectedResourceType);

            return this.CreateResourceSetReaderImplementation(entitySet, expectedResourceType, /*readingParameter*/ true, /*readingDelta*/ false);
        }

        /// <summary>
        /// Asynchronously creates an <see cref="ODataReader" /> to read a resource set in a Uri operation parameter.
        /// </summary>
        /// <param name="entitySet">The entity set we are going to read resources for.</param>
        /// <param name="expectedResourceType">The expected structured type for the items in the resource set.</param>
        /// <returns>Task which when completed returns the newly created <see cref="ODataReader"/>.</returns>
        public override Task<ODataReader> CreateUriParameterResourceSetReaderAsync(IEdmEntitySetBase entitySet, IEdmStructuredType expectedResourceType)
        {
            this.AssertAsynchronous();
            this.VerifyCanCreateODataReader(entitySet, expectedResourceType);

            return TaskUtils.GetTaskForSynchronousOperation(() => this.CreateResourceSetReaderImplementation(entitySet, expectedResourceType, /*readingParameter*/ true, /*readingDelta*/ false));
        }

        /// <summary>
        /// Creates an <see cref="ODataReader" /> to read a resource in a Uri operation parameter.
        /// </summary>
        /// <param name="navigationSource">The navigation source we are going to read resources for.</param>
        /// <param name="expectedResourceType">The expected resource type for the resource to be read.</param>
        /// <returns>The newly created <see cref="ODataReader"/>.</returns>
        public override ODataReader CreateUriParameterResourceReader(IEdmNavigationSource navigationSource, IEdmStructuredType expectedResourceType)
        {
            return this.CreateResourceReader(navigationSource, expectedResourceType);
        }

        /// <summary>
        /// Asynchronously creates an <see cref="ODataReader" /> to read a resource in a Uri operation parameter.
        /// </summary>
        /// <param name="navigationSource">The navigation source we are going to read resources for.</param>
        /// <param name="expectedResourceType">The expected structured type for the resource to be read.</param>
        /// <returns>Task which when completed returns the newly created <see cref="ODataReader"/>.</returns>
        public override Task<ODataReader> CreateUriParameterResourceReaderAsync(IEdmNavigationSource navigationSource, IEdmStructuredType expectedResourceType)
        {
            return this.CreateResourceReaderAsync(navigationSource, expectedResourceType);
        }

        /// <summary>
        /// Create a <see cref="ODataParameterReader"/>.
        /// </summary>
        /// <param name="operation">The operation whose parameters are being read.</param>
        /// <returns>The newly created <see cref="ODataParameterReader"/>.</returns>
        public override ODataParameterReader CreateParameterReader(IEdmOperation operation)
        {
            this.AssertSynchronous();
            this.VerifyCanCreateParameterReader(operation);

            return this.CreateParameterReaderImplementation(operation);
        }

        /// <summary>
        /// Asynchronously create a <see cref="ODataParameterReader"/>.
        /// </summary>
        /// <param name="operation">The operation whose parameters are being read.</param>
        /// <returns>Task which when completed returns the newly created <see cref="ODataParameterReader"/>.</returns>
        public override Task<ODataParameterReader> CreateParameterReaderAsync(IEdmOperation operation)
        {
            this.AssertAsynchronous();
            this.VerifyCanCreateParameterReader(operation);

            // Note that the reading is actually synchronous since we buffer the entire input when getting the stream from the message.
            return TaskUtils.GetTaskForSynchronousOperation(() => this.CreateParameterReaderImplementation(operation));
        }

        /// <summary>
        /// Detects the payload kind(s) from the message stream.
        /// </summary>
        /// <param name="detectionInfo">Additional information available for the payload kind detection.</param>
        /// <returns>An enumerable of zero, one or more payload kinds that were detected from looking at the payload in the message stream.</returns>
        public IEnumerable<ODataPayloadKind> DetectPayloadKind(ODataPayloadKindDetectionInfo detectionInfo)
        {
            Debug.Assert(detectionInfo != null, "detectionInfo != null");
            this.VerifyCanDetectPayloadKind();

            ODataJsonPayloadKindDetectionDeserializer payloadKindDetectionDeserializer = new ODataJsonPayloadKindDetectionDeserializer(this);
            return payloadKindDetectionDeserializer.DetectPayloadKind(detectionInfo);
        }

        /// <summary>
        /// Detects the payload kind(s) from the message stream.
        /// </summary>
        /// <param name="detectionInfo">Additional information available for the payload kind detection.</param>
        /// <returns>A task which returns an enumerable of zero, one or more payload kinds that were detected from looking at the payload in the message stream.</returns>
        public Task<IEnumerable<ODataPayloadKind>> DetectPayloadKindAsync(ODataPayloadKindDetectionInfo detectionInfo)
        {
            Debug.Assert(detectionInfo != null, "detectionInfo != null");
            this.VerifyCanDetectPayloadKind();

            ODataJsonPayloadKindDetectionDeserializer payloadKindDetectionDeserializer = new ODataJsonPayloadKindDetectionDeserializer(this);
            return payloadKindDetectionDeserializer.DetectPayloadKindAsync(detectionInfo);
        }

        /// <summary>
        /// Creates an <see cref="ODataDeltaReader" /> to read a resource set.
        /// </summary>
        /// <param name="entitySet">The entity set we are going to read entities for.</param>
        /// <param name="expectedBaseEntityType">The expected base entity type for the entries in the delta response.</param>
        /// <returns>The newly created <see cref="ODataDeltaReader"/>.</returns>
        internal override ODataDeltaReader CreateDeltaReader(IEdmEntitySetBase entitySet, IEdmEntityType expectedBaseEntityType)
        {
            this.AssertSynchronous();
            this.VerifyCanCreateODataReader(entitySet, expectedBaseEntityType);

            return this.CreateDeltaReaderImplementation(entitySet, expectedBaseEntityType);
        }

        /// <summary>
        /// Asynchronously creates an <see cref="ODataDeltaReader" /> to read a resource set.
        /// </summary>
        /// <param name="entitySet">The entity set we are going to read entities for.</param>
        /// <param name="expectedBaseEntityType">The expected base entity type for the entries in the delta response.</param>
        /// <returns>Task which when completed returns the newly created <see cref="ODataDeltaReader"/>.</returns>
        internal override Task<ODataDeltaReader> CreateDeltaReaderAsync(IEdmEntitySetBase entitySet, IEdmEntityType expectedBaseEntityType)
        {
            this.AssertAsynchronous();
            this.VerifyCanCreateODataReader(entitySet, expectedBaseEntityType);

            // Note that the reading is actually synchronous since we buffer the entire input when getting the stream from the message.
            return TaskUtils.GetTaskForSynchronousOperation(() => this.CreateDeltaReaderImplementation(entitySet, expectedBaseEntityType));
        }

        /// <summary>
        /// Create a <see cref="ODataBatchReader"/>.
        /// </summary>
        /// <returns>The newly created <see cref="ODataCollectionReader"/>.</returns>
        internal override ODataBatchReader CreateBatchReader()
        {
            return this.CreateBatchReaderImplementation(/*synchronous*/ true);
        }

        /// <summary>
        /// Asynchronously create a <see cref="ODataBatchReader"/>.
        /// </summary>
        /// <returns>Task which when completed returns the newly created <see cref="ODataCollectionReader"/>.</returns>
        internal override Task<ODataBatchReader> CreateBatchReaderAsync()
        {
            // Note that the reading is actually synchronous since we buffer the entire input when getting the stream from the message.
            return TaskUtils.GetTaskForSynchronousOperation(() => this.CreateBatchReaderImplementation(/*synchronous*/ false));
        }

        /// <summary>
        /// Read a service document.
        /// This method reads the service document from the input and returns
        /// an <see cref="ODataServiceDocument"/> that represents the read service document.
        /// </summary>
        /// <returns>An <see cref="ODataServiceDocument"/> representing the read service document.</returns>
        internal override ODataServiceDocument ReadServiceDocument()
        {
            this.AssertSynchronous();

            ODataJsonServiceDocumentDeserializer jsonServiceDocumentDeserializer = new ODataJsonServiceDocumentDeserializer(this);
            return jsonServiceDocumentDeserializer.ReadServiceDocument();
        }

        /// <summary>
        /// Asynchronously read a service document.
        /// This method reads the service document from the input and returns
        /// an <see cref="ODataServiceDocument"/> that represents the read service document.
        /// </summary>
        /// <returns>Task which when completed returns an <see cref="ODataServiceDocument"/> representing the read service document.</returns>
        internal override Task<ODataServiceDocument> ReadServiceDocumentAsync()
        {
            this.AssertAsynchronous();

            ODataJsonServiceDocumentDeserializer jsonServiceDocumentDeserializer = new ODataJsonServiceDocumentDeserializer(this);
            return jsonServiceDocumentDeserializer.ReadServiceDocumentAsync();
        }

        /// <summary>
        /// Read a set of top-level entity reference links.
        /// </summary>
        /// <returns>An <see cref="ODataEntityReferenceLinks"/> representing the read links.</returns>
        internal override ODataEntityReferenceLinks ReadEntityReferenceLinks()
        {
            this.AssertSynchronous();

            ODataJsonEntityReferenceLinkDeserializer jsonEntityReferenceLinkDeserializer = new ODataJsonEntityReferenceLinkDeserializer(this);
            return jsonEntityReferenceLinkDeserializer.ReadEntityReferenceLinks();
        }

        /// <summary>
        /// Asynchronously read a set of top-level entity reference links.
        /// </summary>
        /// <returns>Task which when completed returns an <see cref="ODataEntityReferenceLinks"/> representing the read links.</returns>
        internal override Task<ODataEntityReferenceLinks> ReadEntityReferenceLinksAsync()
        {
            this.AssertAsynchronous();

            ODataJsonEntityReferenceLinkDeserializer jsonEntityReferenceLinkDeserializer = new ODataJsonEntityReferenceLinkDeserializer(this);
            return jsonEntityReferenceLinkDeserializer.ReadEntityReferenceLinksAsync();
        }

        /// <summary>
        /// Reads a top-level entity reference link.
        /// </summary>
        /// <returns>An <see cref="ODataEntityReferenceLink"/> representing the read entity reference link.</returns>
        internal override ODataEntityReferenceLink ReadEntityReferenceLink()
        {
            this.AssertSynchronous();
            this.VerifyCanReadEntityReferenceLink();

            ODataJsonEntityReferenceLinkDeserializer jsonEntityReferenceLinkDeserializer = new ODataJsonEntityReferenceLinkDeserializer(this);
            return jsonEntityReferenceLinkDeserializer.ReadEntityReferenceLink();
        }

        /// <summary>
        /// Asynchronously read a top-level entity reference link.
        /// </summary>
        /// <returns>Task which when completed returns an <see cref="ODataEntityReferenceLink"/> representing the read entity reference link.</returns>
        internal override Task<ODataEntityReferenceLink> ReadEntityReferenceLinkAsync()
        {
            this.AssertAsynchronous();
            this.VerifyCanReadEntityReferenceLink();

            ODataJsonEntityReferenceLinkDeserializer jsonEntityReferenceLinkDeserializer = new ODataJsonEntityReferenceLinkDeserializer(this);
            return jsonEntityReferenceLinkDeserializer.ReadEntityReferenceLinkAsync();
        }

        /// <summary>
        /// Perform the actual cleanup work.
        /// </summary>
        /// <param name="disposing">If 'true' this method is called from user code; if 'false' it is called by the runtime.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try
                {
                    this.stream = null;

                    if (this.textReader != null)
                    {
                        this.textReader.Dispose();
                    }
                }
                finally
                {
                    this.textReader = null;
                    this.jsonReader = null;
                }
            }

            base.Dispose(disposing);
        }

        /// <summary>
        /// Helper method to create a TextReader over the message stream. This is needed by the constructor to dispose the message stream if the creation fails
        /// since this is called from the constructor in place where exception handling is not possible.
        /// </summary>
        /// <param name="messageStream">The stream to read data from.</param>
        /// <param name="encoding">The encoding to use to read the input.</param>
        /// <returns>The newly created text reader.</returns>
        private static TextReader CreateTextReader(Stream messageStream, Encoding encoding)
        {
            Debug.Assert(messageStream != null, "messageStream != null");

            try
            {
                return new StreamReader(messageStream, encoding);
            }
            catch (Exception e)
            {
                // Dispose the message stream if we failed to create the input context.
                if (ExceptionUtils.IsCatchableExceptionType(e) && messageStream != null)
                {
                    messageStream.Dispose();
                }

                throw;
            }
        }

        private static IJsonReader CreateJsonReader(IServiceProvider container, TextReader textReader, bool isIeee754Compatible)
        {
            if (container == null)
            {
                return new JsonReader(textReader, isIeee754Compatible);
            }

            var jsonReaderFactory = container.GetRequiredService<IJsonReaderFactory>();
            var jsonReader = jsonReaderFactory.CreateJsonReader(textReader, isIeee754Compatible);
            Debug.Assert(jsonReader != null, "jsonWriter != null");

            return jsonReader;
        }

        /// <summary>
        /// Verifies that CreateParameterReader can be called.
        /// </summary>
        /// <param name="operation">The operation whose parameters are being read.</param>
        private void VerifyCanCreateParameterReader(IEdmOperation operation)
        {
            this.VerifyUserModel();

            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation), Error.Format(SRResources.ODataJsonInputContext_OperationCannotBeNullForCreateParameterReader, "operation"));
            }
        }

        /// <summary>
        /// Verifies that CreateResourceReader, CreateResourceSetReader, CreateDeltaResourceSetReader or CreateDeltaReader can be called.
        /// </summary>
        /// <param name="navigationSource">The navigation source we are going to read resources for.</param>
        /// <param name="structuredType">The expected structured type for the resource/resource set to be read.</param>
        private void VerifyCanCreateODataReader(IEdmNavigationSource navigationSource, IEdmStructuredType structuredType)
        {
            Debug.Assert(navigationSource == null || structuredType != null, "If an navigation source is specified, the structured type must be specified as well.");

            // We require metadata information for reading requests.
            if (!this.ReadingResponse)
            {
                this.VerifyUserModel();

                // TODO: check for entity only
                if (navigationSource == null && (structuredType != null && structuredType.IsODataEntityTypeKind()))
                {
                    throw new ODataException(SRResources.ODataJsonInputContext_NoEntitySetForRequest);
                }
            }

            // We only check that the base type of the entity set is assignable from the specified entity type.
            // If no resource set/resource type is specified in the API, we will read it from the context URI.
            IEdmEntityType entitySetElementType = this.EdmTypeResolver.GetElementType(navigationSource);
            if (navigationSource != null && structuredType != null && !structuredType.IsOrInheritsFrom(entitySetElementType))
            {
                throw new ODataException(Error.Format(SRResources.ODataJsonInputContext_EntityTypeMustBeCompatibleWithEntitySetBaseType, structuredType.FullTypeName(), entitySetElementType.FullName(), navigationSource.FullNavigationSourceName()));
            }
        }

        /// <summary>
        /// Verifies that CreateCollectionReader can be called.
        /// </summary>
        /// <param name="expectedItemTypeReference">The expected type reference for the items in the collection.</param>
        private void VerifyCanCreateCollectionReader(IEdmTypeReference expectedItemTypeReference)
        {
            // We require metadata information for reading requests.
            if (!this.ReadingResponse)
            {
                this.VerifyUserModel();

                if (expectedItemTypeReference == null)
                {
                    throw new ODataException(SRResources.ODataJsonInputContext_ItemTypeRequiredForCollectionReaderInRequests);
                }
            }
        }

        /// <summary>
        /// Verifies that ReadEntityReferenceLink can be called.
        /// </summary>
        private void VerifyCanReadEntityReferenceLink()
        {
            // We require metadata information for reading requests.
            if (!this.ReadingResponse)
            {
                this.VerifyUserModel();
            }
        }

        /// <summary>
        /// Verifies that ReadProperty can be called.
        /// </summary>
        private void VerifyCanReadProperty()
        {
            // We require metadata information for reading requests.
            if (!this.ReadingResponse)
            {
                this.VerifyUserModel();
            }
        }

        /// <summary>
        /// Verifies that DetectPayloadKind can be called.
        /// </summary>
        private void VerifyCanDetectPayloadKind()
        {
            if (!this.ReadingResponse)
            {
                throw new ODataException(SRResources.ODataJsonInputContext_PayloadKindDetectionForRequest);
            }
        }

        /// <summary>
        /// Verifies that a user model is available for reading.
        /// </summary>
        private void VerifyUserModel()
        {
            if (!this.Model.IsUserModel())
            {
                throw new ODataException(SRResources.ODataJsonInputContext_ModelRequiredForReading);
            }
        }

        /// <summary>
        /// Creates an <see cref="ODataReader" /> to read a resource set.
        /// </summary>
        /// <param name="entitySet">The entity set we are going to read resources for.</param>
        /// <param name="expectedResourceType">The expected structured type for the items in the resource set.</param>
        /// <param name="readingParameter">true means reading a resource set in uri operation parameter, false reading a resource set in other payloads.</param>
        /// <param name="readingDelta">true if reading a delta resource set.</param>
        /// <returns>The newly created <see cref="ODataReader"/>.</returns>
        private ODataReader CreateResourceSetReaderImplementation(IEdmEntitySetBase entitySet, IEdmStructuredType expectedResourceType, bool readingParameter, bool readingDelta)
        {
            return new ODataJsonReader(this, entitySet, expectedResourceType, /*readingResourceSet*/ true, readingParameter, readingDelta);
        }

        /// <summary>
        /// Creates an <see cref="ODataDeltaReader" /> to read a resource set.
        /// </summary>
        /// <param name="entitySet">The entity set we are going to read entities for.</param>
        /// <param name="expectedBaseEntityType">The expected base entity type for the entries in the delta response.</param>
        /// <returns>The newly created <see cref="ODataReader"/>.</returns>
        private ODataDeltaReader CreateDeltaReaderImplementation(IEdmEntitySetBase entitySet, IEdmEntityType expectedBaseEntityType)
        {
            return new ODataJsonDeltaReader(this, entitySet, expectedBaseEntityType);
        }

        /// <summary>
        /// Creates an <see cref="ODataReader" /> to read a resource.
        /// </summary>
        /// <param name="navigationSource">The navigation source we are going to read resources for.</param>
        /// <param name="expectedBaseResourceType">The expected structured type for the resource to be read.</param>
        /// <returns>The newly created <see cref="ODataReader"/>.</returns>
        private ODataReader CreateResourceReaderImplementation(IEdmNavigationSource navigationSource, IEdmStructuredType expectedBaseResourceType)
        {
            return new ODataJsonReader(this, navigationSource, expectedBaseResourceType, false, readingDelta: !this.ReadingResponse);
        }

        /// <summary>
        /// Create a <see cref="ODataCollectionReader"/>.
        /// </summary>
        /// <param name="expectedItemTypeReference">The expected type reference for the items in the collection.</param>
        /// <returns>Newly create <see cref="ODataCollectionReader"/>.</returns>
        private ODataCollectionReader CreateCollectionReaderImplementation(IEdmTypeReference expectedItemTypeReference)
        {
            return new ODataJsonCollectionReader(this, expectedItemTypeReference, null /*listener*/);
        }

        /// <summary>
        /// Create a <see cref="ODataParameterReader"/>.
        /// </summary>
        /// <param name="operation">The operation import whose parameters are being read.</param>
        /// <returns>The newly created <see cref="ODataParameterReader"/>.</returns>
        private ODataParameterReader CreateParameterReaderImplementation(IEdmOperation operation)
        {
            return new ODataJsonParameterReader(this, operation);
        }


        /// <summary>
        /// Create a concrete <see cref="ODataJsonBatchReader"/> instance.
        /// </summary>
        /// <param name="synchronous">true if the input should be read synchronously; false if it should be read asynchronously.</param>
        /// <returns>Newly created <see cref="ODataBatchReader"/></returns>
        private ODataBatchReader CreateBatchReaderImplementation(bool synchronous)
        {
            Debug.Assert(this.textReader != null);
            Debug.Assert(this.textReader is StreamReader);

            return new ODataJsonBatchReader(
                this,
                synchronous);
        }
    }
}
