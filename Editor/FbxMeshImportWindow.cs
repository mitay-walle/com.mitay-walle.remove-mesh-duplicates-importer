using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace Core
{
    public sealed class FbxMeshImportWindow : EditorWindow
    {
        private const string SettingsPrefix = "GTR_FBX_MESH_IMPORT:";
        private UnityEngine.Object _model;
        private bool _reuseIdenticalMeshes = true;
        private bool _reuseVertexRotatedIdenticalMeshes;
        private bool _enableLogging;
        private List<FbxMeshObjectProcessingRule> _objectProcessingRules = new();
        private string _searchText = string.Empty;
        private Vector2 _scrollPosition;
        [SerializeField] private TreeViewState<int> _objectTreeViewState;
        private FbxMeshObjectTreeView _objectTreeView;

        internal static void OpenForAsset(UnityEngine.Object model)
        {
            FbxMeshImportWindow window = GetUtilityWindow();
            window._model = model;
            window.LoadSettings();
            window.Focus();
        }

        private static FbxMeshImportWindow GetUtilityWindow()
        {
            FbxMeshImportWindow[] windows = UnityEngine.Resources.FindObjectsOfTypeAll<FbxMeshImportWindow>();
            for (int i = 0; i < windows.Length; i++)
            {
                if (windows[i] != null)
                {
                    windows[i].Focus();
                    return windows[i];
                }
            }

            FbxMeshImportWindow window = CreateInstance<FbxMeshImportWindow>();
            window.titleContent = new GUIContent("Remove Mesh Duplicates Importer");
            window.minSize = new Vector2(360f, 260f);
            window.ShowUtility();
            return window;
        }

        private void OnEnable()
        {
            _objectTreeViewState ??= new TreeViewState<int>();
            _objectTreeView = new FbxMeshObjectTreeView(_objectTreeViewState);
            _objectTreeView.SetSearchText(_searchText);
            if (_model == null && Selection.activeObject != null)
            {
                _model = Selection.activeObject;
            }

            LoadSettings();
        }

        private void OnGUI()
        {
            if (_objectTreeView == null)
            {
                _objectTreeViewState ??= new TreeViewState<int>();
                _objectTreeView = new FbxMeshObjectTreeView(_objectTreeViewState);
                _objectTreeView.SetRules(_objectProcessingRules);
            }

            UnityEngine.Object previousModel = _model;
            _model = EditorGUILayout.ObjectField(new GUIContent("FBX Model", "FBX asset whose import settings will be changed."),
                _model, typeof(GameObject), false);
            if (previousModel != _model)
            {
                LoadSettings();
            }

            using (new EditorGUI.DisabledScope(!IsModelSelected()))
            {
                if (GUILayout.Button(new GUIContent("Save Settings And Reimport",
                        "Store these settings in the FBX importer and reimport the model.")))
                {
                    SaveSettingsAndReimport();
                }
            }

            string previousSearchText = _searchText;
            _searchText = EditorGUILayout.TextField(
                new GUIContent("Search", "Find Mesh objects by separate words. Partial matches are supported."),
                _searchText);
            if (!string.Equals(previousSearchText, _searchText, StringComparison.Ordinal))
            {
                _objectTreeView.SetSearchText(_searchText);
            }

            _reuseIdenticalMeshes = EditorGUILayout.Toggle(
                new GUIContent("Reuse Identical Meshes", "Reuse one Mesh asset when all mesh data is identical."),
                _reuseIdenticalMeshes);

            _reuseVertexRotatedIdenticalMeshes = EditorGUILayout.Toggle(
                new GUIContent("Reuse Vertex-Rotated Meshes",
                    "Compare meshes whose vertex data is rotated. Does not modify object pivots or transforms."),
                _reuseVertexRotatedIdenticalMeshes);

            _enableLogging = EditorGUILayout.Toggle(
                new GUIContent("Enable Logging", "Write FBX mesh import diagnostics to the Unity Console."),
                _enableLogging);

            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);
            EditorGUILayout.LabelField("Object Processing Rules");
            float treeHeight = Mathf.Max(100f, _objectTreeView.VisibleRowCount * _objectTreeView.RowHeight);
            Rect treeRect = GUILayoutUtility.GetRect(0f, treeHeight, GUILayout.ExpandWidth(true));
            _objectTreeView.OnGUI(treeRect);

            EditorGUILayout.EndScrollView();
        }

        private bool IsModelSelected()
        {
            string assetPath = AssetDatabase.GetAssetPath(_model);
            return _model != null && AssetImporter.GetAtPath(assetPath) as ModelImporter;
        }

        private void SaveSettingsAndReimport()
        {
            string assetPath = AssetDatabase.GetAssetPath(_model);
            ModelImporter importer = AssetImporter.GetAtPath(assetPath) as ModelImporter;
            if (importer == null)
            {
                return;
            }

            FbxMeshImportConfiguration configuration = new()
            {
                ReuseIdenticalMeshes = _reuseIdenticalMeshes,
                ReuseVertexRotatedIdenticalMeshes = _reuseVertexRotatedIdenticalMeshes,
                EnableLogging = _enableLogging,
                ObjectProcessingRules = GetChangedObjectRules()
            };

            importer.userData = SettingsPrefix + JsonUtility.ToJson(configuration);
            importer.isReadable = true;
            importer.SaveAndReimport();
        }

        private List<FbxMeshObjectProcessingRule> GetChangedObjectRules()
        {
            List<FbxMeshObjectProcessingRule> changedRules = new();
            for (int i = 0; i < _objectProcessingRules.Count; i++)
            {
                FbxMeshObjectProcessingRule rule = _objectProcessingRules[i];
                if (rule == null || rule.ShouldProcess)
                {
                    continue;
                }

                changedRules.Add(new FbxMeshObjectProcessingRule
                {
                    ShouldProcess = false,
                    ObjectName = rule.ObjectName,
                    ObjectPath = rule.ObjectPath,
                    HierarchyDepth = rule.HierarchyDepth
                });
            }

            return changedRules;
        }

        private void LoadSettings()
        {
            string assetPath = _model == null ? string.Empty : AssetDatabase.GetAssetPath(_model);
            ModelImporter importer = AssetImporter.GetAtPath(assetPath) as ModelImporter;
            if (TryReadConfiguration(importer, out FbxMeshImportConfiguration configuration))
            {
                _reuseIdenticalMeshes = configuration.ReuseIdenticalMeshes;
                _reuseVertexRotatedIdenticalMeshes = configuration.ReuseVertexRotatedIdenticalMeshes;
                _enableLogging = configuration.EnableLogging;
                PopulateObjectRules(configuration.ObjectProcessingRules);
                return;
            }

            PopulateObjectRules(null);
        }

        private void PopulateObjectRules(List<FbxMeshObjectProcessingRule> savedRules)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            Dictionary<string, bool> savedFlagsByPath = new(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, bool> savedFlagsByName = new(StringComparer.OrdinalIgnoreCase);
            if (savedRules != null)
            {
                foreach (FbxMeshObjectProcessingRule savedRule in savedRules)
                {
                    if (savedRule == null) continue;
                    if (!string.IsNullOrEmpty(savedRule.ObjectPath))
                        savedFlagsByPath[savedRule.ObjectPath] = savedRule.ShouldProcess;
                    if (!string.IsNullOrEmpty(savedRule.ObjectName))
                        savedFlagsByName[savedRule.ObjectName] = savedRule.ShouldProcess;
                }
            }

            _objectProcessingRules = new List<FbxMeshObjectProcessingRule>();
            if (_model == null)
            {
                _objectTreeView?.SetRules(_objectProcessingRules);
                return;
            }

            string assetPath = AssetDatabase.GetAssetPath(_model);
            GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (model == null)
            {
                _objectTreeView?.SetRules(_objectProcessingRules);
                return;
            }

            Transform[] transforms = model.GetComponentsInChildren<Transform>(true);
            HashSet<Transform> meshObjects = new();
            for (int i = 0; i < transforms.Length; i++)
            {
                if (HasMesh(transforms[i]))
                    meshObjects.Add(transforms[i]);
            }

            for (int i = 0; i < transforms.Length; i++)
            {
                Transform transform = transforms[i];
                if (!meshObjects.Contains(transform))
                    continue;

                string objectPath = FbxMeshAssetPostprocessor.GetObjectPath(transform, model.transform);
                bool shouldProcess = !savedFlagsByPath.TryGetValue(objectPath, out bool savedFlag) &&
                    !savedFlagsByName.TryGetValue(transform.name, out savedFlag) || savedFlag;
                _objectProcessingRules.Add(new FbxMeshObjectProcessingRule
                {
                    ObjectName = transform.name,
                    ObjectPath = objectPath,
                    HierarchyDepth = GetMeshHierarchyDepth(transform, model.transform, meshObjects),
                    ShouldProcess = shouldProcess
                });
            }

            stopwatch.Stop();
            if (_enableLogging)
                UnityEngine.Debug.Log($"[Remove Mesh Duplicates] Object list processing took {stopwatch.Elapsed.TotalMilliseconds:0} ms.");

            _objectTreeView?.SetRules(_objectProcessingRules);
            _objectTreeView?.SetSearchText(_searchText);
        }

        private static bool HasMesh(Transform transform)
        {
            return transform.GetComponent<MeshFilter>() != null ||
                   transform.GetComponent<SkinnedMeshRenderer>() != null ||
                   transform.GetComponent<MeshCollider>() != null;
        }

        private static int GetMeshHierarchyDepth(Transform transform, Transform modelRoot,
            HashSet<Transform> meshObjects)
        {
            int depth = 0;
            while (transform != modelRoot && transform.parent != null)
            {
                if (meshObjects.Contains(transform.parent))
                    depth++;
                transform = transform.parent;
            }

            return depth;
        }

        internal static bool TryReadConfiguration(ModelImporter importer, out FbxMeshImportConfiguration configuration)
        {
            configuration = null;
            if (importer == null || !importer.userData.StartsWith(SettingsPrefix, StringComparison.Ordinal))
            {
                return false;
            }

            try
            {
                configuration = JsonUtility.FromJson<FbxMeshImportConfiguration>(importer.userData.Substring(SettingsPrefix.Length));
                return configuration != null;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }
    }
}