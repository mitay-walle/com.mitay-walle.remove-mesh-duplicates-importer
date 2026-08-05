using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Core
{
    public sealed class FbxMeshAssetPostprocessor : AssetPostprocessor
    {
        private const int PostprocessOrder = 9999;
        private const float RotatedUvThreshold = 0.1f;
        private static bool EnableLogging;
        private bool _wasReadable;
        private bool _temporarySettingsApplied;
        private readonly Dictionary<string, object> _temporaryImporterSettings = new();

        public override int GetPostprocessOrder()
        {
            return PostprocessOrder;
        }

        private void OnPreprocessModel()
        {
            ModelImporter importer = (ModelImporter)assetImporter;
            if (FbxMeshImportWindow.TryReadConfiguration(importer, out _))
            {
                _wasReadable = importer.isReadable;
                importer.isReadable = true;
            }

            if (FbxMeshImportWindow.TryReadConfiguration(importer,
                                                         out FbxMeshImportConfiguration settings) &&
                settings.ReuseVertexRotatedIdenticalMeshes)
            {
                ApplyTemporaryComparisonSettings(importer);
            }
        }

        private void OnPostprocessModel(GameObject model)
        {
            ModelImporter importer = (ModelImporter)assetImporter;
            if (!FbxMeshImportWindow.TryReadConfiguration(importer, out FbxMeshImportConfiguration settings))
            {
                return;
            }

            EnableLogging = settings.EnableLogging;

            try
            {
                if (settings.ReuseIdenticalMeshes)
                {
                    ReuseIdenticalMeshes(model, settings, assetPath);
                }

                if (settings.ReuseVertexRotatedIdenticalMeshes)
                {
                    ReuseVertexRotatedMeshes(model, settings, assetPath);
                }
            }
            finally
            {
                importer.isReadable = _wasReadable;
                RestoreTemporaryComparisonSettings(importer);
            }
        }

        private void ApplyTemporaryComparisonSettings(ModelImporter importer)
        {
            _temporaryImporterSettings.Clear();
            SaveAndSetEnum(importer, "meshCompression", "Off");
            SaveAndSetEnum(importer, "optimizeMesh", "Nothing");
            SaveAndSetBool(importer, "weldVertices", true);
            SaveAndSetEnum(importer, "importTangents", "None");
            SaveAndSetEnum(importer, "importNormals", "Import");
            _temporarySettingsApplied = true;
        }

        private void RestoreTemporaryComparisonSettings(ModelImporter importer)
        {
            if (!_temporarySettingsApplied)
                return;

            foreach (KeyValuePair<string, object> setting in _temporaryImporterSettings)
            {
                PropertyInfo property = importer.GetType().GetProperty(setting.Key,
                                                                        BindingFlags.Instance | BindingFlags.Public);
                property?.SetValue(importer, setting.Value);
            }

            _temporaryImporterSettings.Clear();
            _temporarySettingsApplied = false;
        }

        private void SaveAndSetBool(ModelImporter importer, string propertyName, bool value)
        {
            PropertyInfo property = importer.GetType().GetProperty(propertyName,
                                                                    BindingFlags.Instance | BindingFlags.Public);
            if (property == null || property.PropertyType != typeof(bool) || !property.CanWrite)
                return;

            _temporaryImporterSettings[propertyName] = property.GetValue(importer);
            property.SetValue(importer, value);
        }

        private void SaveAndSetEnum(ModelImporter importer, string propertyName, string valueName)
        {
            PropertyInfo property = importer.GetType().GetProperty(propertyName,
                                                                    BindingFlags.Instance | BindingFlags.Public);
            if (property == null || !property.PropertyType.IsEnum || !property.CanWrite)
                return;

            object value = Enum.Parse(property.PropertyType, valueName);
            _temporaryImporterSettings[propertyName] = property.GetValue(importer);
            property.SetValue(importer, value);
        }

        private static void ReuseIdenticalMeshes(GameObject model, FbxMeshImportConfiguration settings, string assetPath)
        {
            List<Mesh> uniqueMeshes = new();
            HashSet<Mesh> duplicateMeshes = new();
            Dictionary<int, List<Mesh>> meshesBySignature = new();
            Stopwatch stopwatch = Stopwatch.StartNew();
            MeshFilter[] meshFilters = model.GetComponentsInChildren<MeshFilter>(true);
            foreach (MeshFilter meshFilter in meshFilters)
            {
                if (!ShouldProcessObject(meshFilter.transform, model.transform, settings))
                {
                    continue;
                }

                meshFilter.sharedMesh = FindOrAdd(meshFilter.sharedMesh, uniqueMeshes, meshesBySignature, duplicateMeshes,
                                                   !settings.ReuseVertexRotatedIdenticalMeshes);
            }

            SkinnedMeshRenderer[] skinnedRenderers = model.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            foreach (SkinnedMeshRenderer skinnedRenderer in skinnedRenderers)
            {
                if (!ShouldProcessObject(skinnedRenderer.transform, model.transform, settings))
                {
                    continue;
                }

                skinnedRenderer.sharedMesh = FindOrAdd(skinnedRenderer.sharedMesh, uniqueMeshes, meshesBySignature, duplicateMeshes,
                                                       !settings.ReuseVertexRotatedIdenticalMeshes);
            }

            MeshCollider[] meshColliders = model.GetComponentsInChildren<MeshCollider>(true);
            foreach (MeshCollider meshCollider in meshColliders)
            {
                if (!ShouldProcessObject(meshCollider.transform, model.transform, settings))
                {
                    continue;
                }

                meshCollider.sharedMesh = FindOrAdd(meshCollider.sharedMesh, uniqueMeshes, meshesBySignature, duplicateMeshes,
                                                    !settings.ReuseVertexRotatedIdenticalMeshes);
            }

            foreach (Mesh duplicateMesh in duplicateMeshes)
            {
                Log($"[Remove Mesh Duplicates] DELETE: unused Mesh '{duplicateMesh.name}' from '{assetPath}'.");
                UnityEngine.Object.DestroyImmediate(duplicateMesh);
            }

            stopwatch.Stop();
            Log($"[Remove Mesh Duplicates] Mesh reuse processing took {stopwatch.Elapsed.TotalMilliseconds:0} ms.");
        }

        private static Mesh FindOrAdd(Mesh mesh, List<Mesh> uniqueMeshes,
                                      Dictionary<int, List<Mesh>> meshesBySignature,
                                      HashSet<Mesh> duplicateMeshes, bool logUnique)
        {
            if (mesh == null)
            {
                return null;
            }

            int signature = GetMeshSignature(mesh);
            if (!meshesBySignature.TryGetValue(signature, out List<Mesh> candidates))
            {
                candidates = new List<Mesh>();
                meshesBySignature.Add(signature, candidates);
            }

            for (int i = 0; i < candidates.Count; i++)
            {
                if (MeshContentEquals(mesh, candidates[i]))
                {
                    duplicateMeshes.Add(mesh);
                    Log($"[Remove Mesh Duplicates] MATCH: '{mesh.name}' reuses '{candidates[i].name}' in '{AssetDatabase.GetAssetPath(mesh)}'.");
                    return candidates[i];
                }
            }

            if (logUnique && EnableLogging && candidates.Count > 0)
            {
                Mesh candidate = candidates[0];
                Log($"[Remove Mesh Duplicates] UNIQUE: '{mesh.name}' - {GetMeshDifferenceReason(mesh, candidate)} (against '{candidate.name}').");
            }
            else if (logUnique)
            {
                Log($"[Remove Mesh Duplicates] UNIQUE: '{mesh.name}' - no matching signature.");
            }
            uniqueMeshes.Add(mesh);
            candidates.Add(mesh);
            return mesh;
        }

        private static void ReuseVertexRotatedMeshes(GameObject model,
                                                      FbxMeshImportConfiguration settings,
                                                      string assetPath)
        {
            Dictionary<string, List<MeshTarget>> groups = new(StringComparer.OrdinalIgnoreCase);
            AddRotatedMeshTargets(model.GetComponentsInChildren<MeshFilter>(true), model.transform, settings, groups,
                                  static filter => filter.sharedMesh);
            AddRotatedMeshTargets(model.GetComponentsInChildren<SkinnedMeshRenderer>(true), model.transform, settings, groups,
                                  static renderer => renderer.sharedMesh);
            AddRotatedMeshTargets(model.GetComponentsInChildren<MeshCollider>(true), model.transform, settings, groups,
                                  static collider => collider.sharedMesh);

            HashSet<Mesh> duplicateMeshes = new();
            foreach (List<MeshTarget> targets in groups.Values)
            {
                if (targets.Count < 2)
                    continue;

                List<MeshTarget> representatives = new() { targets[0] };
                for (int i = 1; i < targets.Count; i++)
                {
                    MeshTarget candidate = targets[i];
                    bool matched = false;
                    for (int representativeIndex = 0; representativeIndex < representatives.Count; representativeIndex++)
                    {
                        MeshTarget representative = representatives[representativeIndex];
                        if (candidate.Mesh == representative.Mesh)
                        {
                            matched = true;
                            break;
                        }

                        if (!TryGetVertexRotation(representative.Mesh, candidate.Mesh, out Quaternion rotation,
                                                   out string rejectionReason))
                        {
                            if (!string.IsNullOrEmpty(rejectionReason))
                                Log($"[Remove Mesh Duplicates] ROTATED REJECT: '{candidate.Mesh.name}' - {rejectionReason} (against '{representative.Mesh.name}').");
                            continue;
                        }

                        candidate.Assign(representative.Mesh);
                        candidate.Transform.localRotation *= rotation;
                        duplicateMeshes.Add(candidate.Mesh);
                        Log($"[Remove Mesh Duplicates] ROTATED MATCH: '{candidate.Mesh.name}' reuses '{representative.Mesh.name}' with rotation {rotation.eulerAngles} in '{assetPath}'.");
                        matched = true;
                        break;
                    }

                    if (!matched)
                        representatives.Add(candidate);
                }
            }

            foreach (Mesh duplicateMesh in duplicateMeshes)
                UnityEngine.Object.DestroyImmediate(duplicateMesh);
        }

        private static void AddRotatedMeshTargets<T>(T[] components, Transform modelRoot,
                                                     FbxMeshImportConfiguration settings,
                                                     Dictionary<string, List<MeshTarget>> groups,
                                                     Func<T, Mesh> getMesh)
            where T : Component
        {
            for (int i = 0; i < components.Length; i++)
            {
                T component = components[i];
                if (!ShouldProcessObject(component.transform, modelRoot, settings) || getMesh(component) == null)
                    continue;

                Mesh mesh = getMesh(component);
                string groupName = GetRotatedMeshGroupName(mesh.name);
                if (!groups.TryGetValue(groupName, out List<MeshTarget> targets))
                {
                    targets = new List<MeshTarget>();
                    groups.Add(groupName, targets);
                }

                targets.Add(new MeshTarget(component.transform, mesh, assignedMesh => AssignMesh(component, assignedMesh)));
            }
        }

        private static void AssignMesh<T>(T component, Mesh mesh) where T : Component
        {
            if (component is MeshFilter filter)
                filter.sharedMesh = mesh;
            else if (component is SkinnedMeshRenderer renderer)
                renderer.sharedMesh = mesh;
            else if (component is MeshCollider collider)
                collider.sharedMesh = mesh;
        }

        private static string GetRotatedMeshGroupName(string meshName)
        {
            int separator = meshName.LastIndexOfAny(new[] { '_', '-', ' ', '.' });
            if (separator < 0 || separator == meshName.Length - 1)
                return meshName;

            for (int i = separator + 1; i < meshName.Length; i++)
                if (!char.IsDigit(meshName[i]))
                    return meshName;

            return meshName.Substring(0, separator);
        }

        private static bool TryGetVertexRotation(Mesh source, Mesh target, out Quaternion rotation,
                                                  out string rejectionReason)
        {
            rotation = Quaternion.identity;
            rejectionReason = null;
            if (source.vertexCount != target.vertexCount)
            {
                return TryGetSplitVertexRotation(source, target, out rotation, out rejectionReason);
            }

            if (!MeshNonVertexDataEquals(source, target, out rejectionReason))
                return false;

            Vector3 sourceCenter = GetCenter(source.vertices);
            Vector3 targetCenter = GetCenter(target.vertices);
            Vector3 sourceA = default;
            Vector3 sourceB = default;
            int sourceBIndex = -1;
            bool foundBasis = false;
            Vector3[] sourceVertices = source.vertices;
            Vector3[] targetVertices = target.vertices;
            for (int i = 1; i < sourceVertices.Length && !foundBasis; i++)
            {
                for (int j = i + 1; j < sourceVertices.Length; j++)
                {
                    Vector3 a = sourceVertices[i] - sourceVertices[0];
                    Vector3 b = sourceVertices[j] - sourceVertices[0];
                    if (Vector3.Cross(a, b).sqrMagnitude > 0.000001f)
                    {
                        sourceA = a;
                        sourceB = b;
                        sourceBIndex = j;
                        foundBasis = true;
                        break;
                    }
                }
            }

            if (!foundBasis)
            {
                rejectionReason = "vertices do not form a non-collinear rotation basis";
                return false;
            }

            Vector3 targetA = targetVertices[1] - targetVertices[0];
            Vector3 targetB = targetVertices[sourceBIndex] - targetVertices[0];
            Quaternion sourceFrame = CreateFrame(sourceA, sourceB);
            Quaternion targetFrame = CreateFrame(targetA, targetB);
            rotation = targetFrame * Quaternion.Inverse(sourceFrame);

            Vector3 centerOffset = targetCenter - rotation * sourceCenter;
            if (centerOffset.sqrMagnitude > 0.000001f)
            {
                rejectionReason = "vertex rotation also requires a translation";
                return false;
            }

            for (int i = 0; i < sourceVertices.Length; i++)
            {
                if ((rotation * sourceVertices[i] - targetVertices[i]).sqrMagnitude > 0.000001f)
                {
                    rejectionReason = $"vertex {i} does not match after rotation";
                    return false;
                }
            }

            if (!RotatedVectorArraysEqual(source.normals, target.normals, rotation) ||
                !RotatedVectorArraysEqual(source.tangents, target.tangents, rotation))
            {
                rejectionReason = "normals or tangents do not match after rotation";
                return false;
            }

            return true;
        }

        private static bool RotatedVectorArraysEqual(Vector3[] source, Vector3[] target, Quaternion rotation)
        {
            if (source == null || target == null)
                return source == null && target == null;
            if (source.Length != target.Length)
                return false;

            for (int i = 0; i < source.Length; i++)
                if ((rotation * source[i] - target[i]).sqrMagnitude > 0.000001f)
                    return false;
            return true;
        }

        private static bool RotatedVectorArraysEqual(Vector4[] source, Vector4[] target, Quaternion rotation)
        {
            if (source == null || target == null)
                return source == null && target == null;
            if (source.Length != target.Length)
                return false;

            for (int i = 0; i < source.Length; i++)
            {
                Vector3 rotated = rotation * new Vector3(source[i].x, source[i].y, source[i].z);
                if ((rotated - new Vector3(target[i].x, target[i].y, target[i].z)).sqrMagnitude > 0.000001f ||
                    Mathf.Abs(source[i].w - target[i].w) > 0.000001f)
                    return false;
            }
            return true;
        }

        private static Quaternion CreateFrame(Vector3 first, Vector3 second)
        {
            Vector3 x = first.normalized;
            Vector3 z = Vector3.Cross(x, second).normalized;
            Vector3 y = Vector3.Cross(z, x).normalized;
            return Quaternion.LookRotation(z, y);
        }

        private static bool TryGetSplitVertexRotation(Mesh source, Mesh target,
                                                       out Quaternion rotation,
                                                       out string rejectionReason)
        {
            rotation = Quaternion.identity;
            rejectionReason = null;
            if (source.subMeshCount != target.subMeshCount)
            {
                rejectionReason = $"vertex count {source.vertexCount} != {target.vertexCount}; subMesh count differs";
                return false;
            }

            for (int i = 0; i < source.subMeshCount; i++)
            {
                if (source.GetIndexCount(i) != target.GetIndexCount(i))
                {
                    rejectionReason = $"vertex count {source.vertexCount} != {target.vertexCount}; subMesh {i} index count differs";
                    return false;
                }
            }

            List<Vector3> sourceUnique = GetUniqueVertices(source.vertices);
            List<Vector3> targetUnique = GetUniqueVertices(target.vertices);
            if (sourceUnique.Count != targetUnique.Count)
            {
                rejectionReason = $"vertex count {source.vertexCount} != {target.vertexCount}; unique positions {sourceUnique.Count} != {targetUnique.Count}";
                return false;
            }

            if (sourceUnique.Count < 3)
            {
                rejectionReason = "not enough unique positions to determine rotation";
                return false;
            }

            Vector3 sourceCenter = GetCenter(sourceUnique.ToArray());
            Vector3 targetCenter = GetCenter(targetUnique.ToArray());
            GetRotationBasis(sourceUnique, sourceCenter, out Vector3 sourceA, out Vector3 sourceB);
            GetRotationBasis(targetUnique, targetCenter, out Vector3 targetA, out Vector3 targetB);
            rotation = CreateFrame(targetA, targetB) * Quaternion.Inverse(CreateFrame(sourceA, sourceB));

            if (!PositionSetsMatch(sourceUnique, targetUnique, sourceCenter, targetCenter, rotation))
            {
                rejectionReason = $"vertex count {source.vertexCount} != {target.vertexCount}; unique positions do not match after rotation";
                return false;
            }

            return true;
        }

        private static List<Vector3> GetUniqueVertices(Vector3[] vertices)
        {
            const float PositionToleranceSqr = 0.000001f;
            List<Vector3> uniqueVertices = new();
            for (int i = 0; i < vertices.Length; i++)
            {
                bool alreadyAdded = false;
                for (int j = 0; j < uniqueVertices.Count; j++)
                {
                    if ((vertices[i] - uniqueVertices[j]).sqrMagnitude <= PositionToleranceSqr)
                    {
                        alreadyAdded = true;
                        break;
                    }
                }

                if (!alreadyAdded)
                    uniqueVertices.Add(vertices[i]);
            }

            return uniqueVertices;
        }

        private static void GetRotationBasis(List<Vector3> vertices, Vector3 center,
                                             out Vector3 first, out Vector3 second)
        {
            int firstIndex = 0;
            float farthestDistance = -1f;
            for (int i = 0; i < vertices.Count; i++)
            {
                float distance = (vertices[i] - center).sqrMagnitude;
                if (distance > farthestDistance)
                {
                    farthestDistance = distance;
                    firstIndex = i;
                }
            }

            int secondIndex = firstIndex == 0 ? 1 : 0;
            farthestDistance = -1f;
            for (int i = 0; i < vertices.Count; i++)
            {
                float distance = (vertices[i] - vertices[firstIndex]).sqrMagnitude;
                if (i != firstIndex && distance > farthestDistance)
                {
                    farthestDistance = distance;
                    secondIndex = i;
                }
            }

            first = vertices[firstIndex] - center;
            second = vertices[secondIndex] - center;
        }

        private static bool PositionSetsMatch(List<Vector3> source, List<Vector3> target,
                                              Vector3 sourceCenter, Vector3 targetCenter,
                                              Quaternion rotation)
        {
            const float PositionToleranceSqr = 0.000001f;
            bool[] matched = new bool[target.Count];
            for (int i = 0; i < source.Count; i++)
            {
                Vector3 rotated = rotation * (source[i] - sourceCenter) + targetCenter;
                int closestIndex = -1;
                float closestDistance = PositionToleranceSqr;
                for (int j = 0; j < target.Count; j++)
                {
                    if (matched[j])
                        continue;
                    float distance = (rotated - target[j]).sqrMagnitude;
                    if (distance <= closestDistance)
                    {
                        closestDistance = distance;
                        closestIndex = j;
                    }
                }

                if (closestIndex < 0)
                    return false;
                matched[closestIndex] = true;
            }

            return true;
        }

        private static Vector3 GetCenter(Vector3[] vertices)
        {
            Vector3 center = default;
            for (int i = 0; i < vertices.Length; i++)
                center += vertices[i];
            return vertices.Length == 0 ? center : center / vertices.Length;
        }

        private static bool MeshNonVertexDataEquals(Mesh source, Mesh target, out string rejectionReason)
        {
            rejectionReason = null;
            if (source.subMeshCount != target.subMeshCount)
            {
                rejectionReason = $"subMesh count {source.subMeshCount} != {target.subMeshCount}";
                return false;
            }

            if (source.indexFormat != target.indexFormat)
            {
                rejectionReason = "index format differs";
                return false;
            }

            if (!RejectUvArray(source.uv, target.uv, "uv", out rejectionReason)) return false;
            if (!RejectUvArray(source.uv2, target.uv2, "uv2", out rejectionReason)) return false;
            if (!RejectUvArray(source.uv3, target.uv3, "uv3", out rejectionReason)) return false;
            if (!RejectUvArray(source.uv4, target.uv4, "uv4", out rejectionReason)) return false;
            if (!RejectUvArray(source.uv5, target.uv5, "uv5", out rejectionReason)) return false;
            if (!RejectUvArray(source.uv6, target.uv6, "uv6", out rejectionReason)) return false;
            if (!RejectUvArray(source.uv7, target.uv7, "uv7", out rejectionReason)) return false;
            if (!RejectUvArray(source.uv8, target.uv8, "uv8", out rejectionReason)) return false;
            if (!RejectArray(source.colors32, target.colors32, "colors", out rejectionReason)) return false;
            if (!RejectArray(source.boneWeights, target.boneWeights, "bone weights", out rejectionReason)) return false;
            if (!RejectArray(source.bindposes, target.bindposes, "bindposes", out rejectionReason)) return false;
            if (!SubMeshesEqual(source, target)) return Reject("submesh topology or indices differ", out rejectionReason);
            return true;
        }

        private static bool RejectArray<T>(T[] first, T[] second, string name, out string rejectionReason)
        {
            rejectionReason = GetArrayDifferenceReason(first, second, name);
            return rejectionReason == null;
        }

        private static bool RejectUvArray(Vector2[] first, Vector2[] second, string name,
                                          out string rejectionReason)
        {
            rejectionReason = null;
            if (first == null || second == null)
            {
                rejectionReason = first == null && second == null ? null : name + " null state differs";
                return rejectionReason == null;
            }

            if (first.Length != second.Length)
            {
                rejectionReason = $"{name} count {first.Length} != {second.Length}";
                return false;
            }

            float thresholdSqr = RotatedUvThreshold * RotatedUvThreshold;
            for (int i = 0; i < first.Length; i++)
            {
                if ((first[i] - second[i]).sqrMagnitude > thresholdSqr + 0.000001f)
                {
                    rejectionReason = $"{name}[{i}] differs: {first[i]} != {second[i]} (threshold {RotatedUvThreshold:0.###})";
                    return false;
                }
            }

            return true;
        }

        private static bool Reject(string reason, out string rejectionReason)
        {
            rejectionReason = reason;
            return false;
        }

        private sealed class MeshTarget
        {
            public readonly Transform Transform;
            public readonly Mesh Mesh;
            private readonly Action<Mesh> _assign;

            public MeshTarget(Transform transform, Mesh mesh, Action<Mesh> assign)
            {
                Transform = transform;
                Mesh = mesh;
                _assign = assign;
            }

            public void Assign(Mesh mesh) => _assign(mesh);
        }

        private static int GetMeshSignature(Mesh mesh)
        {
            unchecked
            {
                int hash = 17;
                AddArrayHash(ref hash, mesh.vertices);
                AddArrayHash(ref hash, mesh.normals);
                AddArrayHash(ref hash, mesh.tangents);
                AddArrayHash(ref hash, mesh.colors32);
                AddArrayHash(ref hash, mesh.boneWeights);
                AddArrayHash(ref hash, mesh.bindposes);
                AddArrayHash(ref hash, mesh.uv);
                AddArrayHash(ref hash, mesh.uv2);
                AddArrayHash(ref hash, mesh.uv3);
                AddArrayHash(ref hash, mesh.uv4);
                AddArrayHash(ref hash, mesh.uv5);
                AddArrayHash(ref hash, mesh.uv6);
                AddArrayHash(ref hash, mesh.uv7);
                AddArrayHash(ref hash, mesh.uv8);
                hash = hash * 31 + mesh.subMeshCount;
                hash = hash * 31 + (int)mesh.indexFormat;
                for (int i = 0; i < mesh.subMeshCount; i++)
                {
                    hash = hash * 31 + (int)mesh.GetTopology(i);
                    AddArrayHash(ref hash, mesh.GetIndices(i));
                }

                return hash;
            }
        }

        private static void AddArrayHash<T>(ref int hash, T[] values)
        {
            unchecked
            {
                hash = hash * 31 + (values?.Length ?? -1);
                if (values == null) return;
                for (int i = 0; i < values.Length; i++)
                    hash = hash * 31 + EqualityComparer<T>.Default.GetHashCode(values[i]);
            }
        }

        private static bool ShouldProcessObject(Transform target, Transform modelRoot,
                                                FbxMeshImportConfiguration settings)
        {
            return !TryGetObjectRule(target, modelRoot, settings, out FbxMeshObjectProcessingRule rule) || rule.ShouldProcess;
        }

        private static bool TryGetObjectRule(Transform target, Transform modelRoot,
                                             FbxMeshImportConfiguration settings,
                                             out FbxMeshObjectProcessingRule rule)
        {
            rule = null;
            if (settings.ObjectProcessingRules == null)
            {
                return false;
            }

            for (int i = settings.ObjectProcessingRules.Count - 1; i >= 0; i--)
            {
                FbxMeshObjectProcessingRule candidate = settings.ObjectProcessingRules[i];
                if (candidate == null) continue;
                if (!string.IsNullOrEmpty(candidate.ObjectPath) &&
                    string.Equals(candidate.ObjectPath, GetObjectPath(target, modelRoot), StringComparison.OrdinalIgnoreCase))
                {
                    rule = candidate;
                    return true;
                }

                if (string.IsNullOrEmpty(candidate.ObjectPath) &&
                    string.Equals(candidate.ObjectName, target.name, StringComparison.OrdinalIgnoreCase))
                {
                    rule = candidate;
                    return true;
                }
            }

            return false;
        }

        internal static string GetObjectPath(Transform target, Transform modelRoot)
        {
            if (target == null) return string.Empty;

            List<string> parts = new();
            while (target != null)
            {
                string part = target.name;
                if (target.parent != null)
                {
                    int sameNameIndex = 0;
                    for (int i = 0; i < target.GetSiblingIndex(); i++)
                        if (target.parent.GetChild(i).name == target.name) sameNameIndex++;
                    if (sameNameIndex > 0) part += "[" + sameNameIndex + "]";
                }

                parts.Insert(0, part);
                if (target == modelRoot) break;
                target = target.parent;
            }

            return string.Join("/", parts);
        }

        private static bool MeshContentEquals(Mesh first, Mesh second)
        {
            if (first.vertexCount != second.vertexCount)
            {
                LogMismatch(first, second, $"vertexCount {first.vertexCount} != {second.vertexCount}");
                return false;
            }

            if (first.subMeshCount != second.subMeshCount)
            {
                LogMismatch(first, second, $"subMeshCount {first.subMeshCount} != {second.subMeshCount}");
                return false;
            }

            if (first.indexFormat != second.indexFormat)
            {
                LogMismatch(first, second, $"indexFormat {first.indexFormat} != {second.indexFormat}");
                return false;
            }

            if (!CompareArray(first, second, "vertices", first.vertices, second.vertices))
            {
                return false;
            }

            if (!CompareArray(first, second, "normals", first.normals, second.normals))
            {
                return false;
            }

            if (!CompareArray(first, second, "tangents", first.tangents, second.tangents))
            {
                return false;
            }

            if (!CompareArray(first, second, "colors", first.colors32, second.colors32))
            {
                return false;
            }

            if (!CompareArray(first, second, "boneWeights", first.boneWeights, second.boneWeights))
            {
                return false;
            }

            if (!CompareArray(first, second, "bindposes", first.bindposes, second.bindposes))
            {
                return false;
            }

            if (!CompareArray(first, second, "uv", first.uv, second.uv) ||
                !CompareArray(first, second, "uv2", first.uv2, second.uv2) ||
                !CompareArray(first, second, "uv3", first.uv3, second.uv3) ||
                !CompareArray(first, second, "uv4", first.uv4, second.uv4) ||
                !CompareArray(first, second, "uv5", first.uv5, second.uv5) ||
                !CompareArray(first, second, "uv6", first.uv6, second.uv6) ||
                !CompareArray(first, second, "uv7", first.uv7, second.uv7) ||
                !CompareArray(first, second, "uv8", first.uv8, second.uv8))
            {
                return false;
            }

            if (!SubMeshesEqual(first, second))
            {
                LogMismatch(first, second, "submesh topology or indices");
                return false;
            }

            return true;
        }

        private static string GetMeshDifferenceReason(Mesh first, Mesh second)
        {
            if (first.vertexCount != second.vertexCount)
                return $"vertex count {first.vertexCount} != {second.vertexCount}";
            if (first.subMeshCount != second.subMeshCount)
                return $"subMesh count {first.subMeshCount} != {second.subMeshCount}";
            if (first.indexFormat != second.indexFormat)
                return "index format differs";

            string reason = GetArrayDifferenceReason(first.vertices, second.vertices, "vertex positions");
            if (reason != null) return reason;
            reason = GetArrayDifferenceReason(first.normals, second.normals, "normals");
            if (reason != null) return reason;
            reason = GetArrayDifferenceReason(first.tangents, second.tangents, "tangents");
            if (reason != null) return reason;
            reason = GetArrayDifferenceReason(first.colors32, second.colors32, "colors");
            if (reason != null) return reason;
            reason = GetArrayDifferenceReason(first.boneWeights, second.boneWeights, "bone weights");
            if (reason != null) return reason;
            reason = GetArrayDifferenceReason(first.bindposes, second.bindposes, "bindposes");
            if (reason != null) return reason;
            reason = GetArrayDifferenceReason(first.uv, second.uv, "uv");
            if (reason != null) return reason;
            reason = GetArrayDifferenceReason(first.uv2, second.uv2, "uv2");
            if (reason != null) return reason;
            reason = GetArrayDifferenceReason(first.uv3, second.uv3, "uv3");
            if (reason != null) return reason;
            reason = GetArrayDifferenceReason(first.uv4, second.uv4, "uv4");
            if (reason != null) return reason;
            reason = GetArrayDifferenceReason(first.uv5, second.uv5, "uv5");
            if (reason != null) return reason;
            reason = GetArrayDifferenceReason(first.uv6, second.uv6, "uv6");
            if (reason != null) return reason;
            reason = GetArrayDifferenceReason(first.uv7, second.uv7, "uv7");
            if (reason != null) return reason;
            reason = GetArrayDifferenceReason(first.uv8, second.uv8, "uv8");
            if (reason != null) return reason;
            return SubMeshesEqual(first, second) ? "unknown mesh data differs" : "submesh topology or indices";
        }

        private static string GetArrayDifferenceReason<T>(T[] first, T[] second, string name)
        {
            if (first == null || second == null)
                return first == null && second == null ? null : name + " null state differs";
            if (first.Length != second.Length)
                return $"{name} count {first.Length} != {second.Length}";
            for (int i = 0; i < first.Length; i++)
            {
                if (!EqualityComparer<T>.Default.Equals(first[i], second[i]))
                    return $"{name}[{i}] differs: {first[i]} != {second[i]}";
            }
            return null;
        }

        private static void LogMismatch(Mesh first, Mesh second, string reason)
        {
            Log($"[Remove Mesh Duplicates] DIFFERENT: '{first.name}' vs '{second.name}' - {reason}.");
        }

        private static void Log(string message)
        {
            if (EnableLogging)
            {
                Debug.Log(message);
            }
        }

        private static bool CompareArray<T>(Mesh first, Mesh second, string attributeName, T[] firstValues, T[] secondValues)
        {
            if (firstValues == null || secondValues == null)
            {
                if (firstValues == null && secondValues == null)
                {
                    return true;
                }

                LogMismatch(first, second, $"{attributeName}: one array is null");
                return false;
            }

            if (firstValues.Length != secondValues.Length)
            {
                LogMismatch(first, second, $"{attributeName} length {firstValues.Length} != {secondValues.Length}");
                return false;
            }

            for (int index = 0; index < firstValues.Length; index++)
            {
                if (!EqualityComparer<T>.Default.Equals(firstValues[index], secondValues[index]))
                {
                    LogMismatch(first, second,
                                $"{attributeName}[{index}] '{firstValues[index]}' != '{secondValues[index]}'");
                    return false;
                }
            }

            return true;
        }

        private static bool SubMeshesEqual(Mesh first, Mesh second)
        {
            for (int i = 0; i < first.subMeshCount; i++)
            {
                if (first.GetTopology(i) != second.GetTopology(i) || !ArrayEquals(first.GetIndices(i), second.GetIndices(i)))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool ArrayEquals<T>(T[] first, T[] second)
        {
            if (first == null || second == null)
            {
                return first == null && second == null;
            }

            if (first.Length != second.Length)
            {
                return false;
            }

            for (int i = 0; i < first.Length; i++)
            {
                if (!EqualityComparer<T>.Default.Equals(first[i], second[i]))
                {
                    return false;
                }
            }

            return true;
        }
    }

}
