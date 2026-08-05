using System;
using System.Collections.Generic;

namespace Core
{
    [Serializable]
    public sealed class FbxMeshImportConfiguration
    {
        public bool ReuseIdenticalMeshes = true;
        public bool ReuseVertexRotatedIdenticalMeshes;
        public bool EnableLogging;
        public List<FbxMeshObjectProcessingRule> ObjectProcessingRules = new();
    }
}
