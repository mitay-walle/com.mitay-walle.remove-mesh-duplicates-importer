using System;

namespace Core
{
    [Serializable]
    public sealed class FbxMeshObjectProcessingRule
    {
        public bool ShouldProcess = true;
        public string ObjectName;
        public string ObjectPath;
        public int HierarchyDepth;
    }
}
