﻿//---------------------------------------------------------------------
// <copyright file="AmbiguousOperationBindingTests.cs" company="Microsoft">
//      Copyright (C) Microsoft Corporation. All rights reserved. See License.txt in the project root for license information.
// </copyright>
//---------------------------------------------------------------------

using Xunit;

namespace Microsoft.OData.Edm.Tests.Library
{
    public class AmbiguousOperationBindingTests
    {
        [Fact]
        public void AmbigiousOperationBindingShouldReferToFirstOperationAlwaysWhenNotNull()
        {
            var action1 = new EdmAction("DS", "name", EdmCoreModel.Instance.GetBoolean(false));
            action1.AddParameter("param", EdmCoreModel.Instance.GetBoolean(false));
            var function = new EdmFunction("DS2", "name2", EdmCoreModel.Instance.GetBoolean(false), true, new EdmPathExpression("path1"), true);
            AmbiguousOperationBinding ambigiousOperationBinding = new AmbiguousOperationBinding(action1, function);
            Assert.Equal("DS", ambigiousOperationBinding.Namespace);
            Assert.Equal("name", ambigiousOperationBinding.Name);
            Assert.Null(ambigiousOperationBinding.ReturnType);
            Assert.Single(ambigiousOperationBinding.Parameters);
            Assert.Equal(EdmSchemaElementKind.Action, ambigiousOperationBinding.SchemaElementKind);
            Assert.False(ambigiousOperationBinding.IsBound);
            Assert.Null(ambigiousOperationBinding.EntitySetPath);
        }
    }
}
