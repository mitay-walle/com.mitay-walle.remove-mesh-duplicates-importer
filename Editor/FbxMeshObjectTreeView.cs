using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace Core
{
    internal sealed class FbxMeshObjectTreeView : TreeView
    {
        private readonly List<FbxMeshObjectProcessingRule> _rules = new();
        private readonly Dictionary<int, FbxMeshObjectProcessingRule> _rulesById = new();
        private readonly Dictionary<int, FbxMeshObjectTreeViewItem> _itemsById = new();
        private string _searchText = string.Empty;
        private GUIStyle _hierarchyViewItem;
        private int _visibleRowCount;

        public float RowHeight => rowHeight;
        public int VisibleRowCount => _visibleRowCount;

        public FbxMeshObjectTreeView(TreeViewState state)
            : base(state)
        {
            rowHeight = EditorGUIUtility.singleLineHeight;
            Reload();
        }

        public void SetRules(List<FbxMeshObjectProcessingRule> rules)
        {
            _rules.Clear();
            _rulesById.Clear();
            _itemsById.Clear();
            if (rules != null)
            {
                _rules.AddRange(rules);
            }

            Reload();
        }

        public void SetSearchText(string searchText)
        {
            searchText ??= string.Empty;
            if (string.Equals(_searchText, searchText, System.StringComparison.Ordinal))
            {
                return;
            }

            _searchText = searchText;
            Reload();
        }

        protected override TreeViewItem BuildRoot()
        {
            TreeViewItem root = new(0, -1, "Root");
            root.children = new List<TreeViewItem>();
            List<TreeViewItem> items = new();
            HashSet<int> includedRuleIndexes = GetIncludedRuleIndexes();
            _visibleRowCount = includedRuleIndexes.Count;
            _rulesById.Clear();
            _itemsById.Clear();

            for (int i = 0; i < _rules.Count; i++)
            {
                if (!includedRuleIndexes.Contains(i))
                {
                    continue;
                }

                FbxMeshObjectProcessingRule rule = _rules[i];
                int depth = Mathf.Max(0, rule.HierarchyDepth);
                FbxMeshObjectTreeViewItem item = new(i + 1, depth, rule.ObjectName, rule);
                items.Add(item);
                _rulesById[item.id] = rule;
                _itemsById[item.id] = item;
            }

            SetupParentsAndChildrenFromDepths(root, items);
            return root;
        }

        protected override void RowGUI(RowGUIArgs args)
        {
            FbxMeshObjectProcessingRule rule = ((FbxMeshObjectTreeViewItem)args.item).Rule;
            if (rule == null)
            {
                base.RowGUI(args);
                return;
            }

            float indent = GetContentIndent(args.item);
            Rect foldoutRect = new(args.rowRect.x + indent - 16f, args.rowRect.y, 16f, args.rowRect.height);
            if (args.item.hasChildren)
            {
                bool expanded = EditorGUI.Foldout(foldoutRect, IsExpanded(args.item.id), GUIContent.none);
                if (expanded != IsExpanded(args.item.id))
                {
                    SetExpanded(args.item.id, expanded);
                }
            }

            Rect contentRect = args.rowRect;
            contentRect.x += indent;
            contentRect.width -= indent;

            Rect toggleRect = new(contentRect.x, contentRect.y, 16f, contentRect.height);
            Rect labelRect = new(contentRect.x + 16f, contentRect.y,
                                 contentRect.width - 16f, contentRect.height);
            bool previousMixedValue = EditorGUI.showMixedValue;
            EditorGUI.showMixedValue = HasMixedDescendantFlags(args.item);
            EditorGUI.BeginChangeCheck();
            bool shouldProcess = EditorGUI.Toggle(toggleRect, rule.ShouldProcess);
            if (EditorGUI.EndChangeCheck())
            {
                rule.ShouldProcess = shouldProcess;
                HashSet<int> selectedIds = new(GetSelection());
                if (selectedIds.Count > 1 && selectedIds.Contains(args.item.id))
                {
                    foreach (int selectedId in selectedIds)
                    {
                        if (_rulesById.TryGetValue(selectedId, out FbxMeshObjectProcessingRule selectedRule))
                        {
                            selectedRule.ShouldProcess = shouldProcess;
                        }

                        if (_itemsById.TryGetValue(selectedId, out FbxMeshObjectTreeViewItem selectedItem))
                        {
                            SetDescendantFlags(selectedItem, shouldProcess);
                        }
                    }
                }
                else
                {
                    SetDescendantFlags(args.item, shouldProcess);
                }
            }
            EditorGUI.showMixedValue = previousMixedValue;

            _hierarchyViewItem ??= new GUIStyle(EditorGUIUtility.GetBuiltinSkin(EditorSkin.Inspector)
                .FindStyle("hierarchyViewItem") ?? EditorStyles.label)
            {
                richText = true
            };
            GUI.Label(labelRect, new GUIContent(GetHighlightedName(rule.ObjectName), rule.ObjectPath),
                      _hierarchyViewItem);
        }

        private HashSet<int> GetIncludedRuleIndexes()
        {
            HashSet<int> includedIndexes = new();
            string[] searchTerms = GetSearchTerms();
            if (searchTerms.Length == 0)
            {
                for (int i = 0; i < _rules.Count; i++)
                    includedIndexes.Add(i);
                return includedIndexes;
            }

            List<int> ancestors = new();
            for (int i = 0; i < _rules.Count; i++)
            {
                int depth = Mathf.Max(0, _rules[i].HierarchyDepth);
                while (ancestors.Count > depth)
                    ancestors.RemoveAt(ancestors.Count - 1);

                if (MatchesSearch(_rules[i].ObjectName, searchTerms))
                {
                    includedIndexes.Add(i);
                    for (int ancestorIndex = 0; ancestorIndex < ancestors.Count; ancestorIndex++)
                        includedIndexes.Add(ancestors[ancestorIndex]);
                }

                while (ancestors.Count <= depth)
                    ancestors.Add(-1);
                ancestors[depth] = i;
                if (ancestors.Count > depth + 1)
                    ancestors.RemoveRange(depth + 1, ancestors.Count - depth - 1);
            }

            return includedIndexes;
        }

        private string GetHighlightedName(string objectName)
        {
            string[] searchTerms = GetSearchTerms();
            if (searchTerms.Length == 0)
                return objectName;

            string pattern = string.Join("|", searchTerms);
            string escapedName = objectName.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
            string highlightColor = EditorGUIUtility.isProSkin ? "#FFD54F" : "#B05A00";
            return Regex.Replace(escapedName, pattern,
                                 match => "<color=" + highlightColor + ">" + match.Value + "</color>",
                                 RegexOptions.IgnoreCase);
        }

        private string[] GetSearchTerms()
        {
            string[] rawTerms = _searchText.Split((char[])null,
                                                    System.StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < rawTerms.Length; i++)
                rawTerms[i] = Regex.Escape(rawTerms[i]);
            return rawTerms;
        }

        private static bool MatchesSearch(string objectName, string[] searchTerms)
        {
            for (int i = 0; i < searchTerms.Length; i++)
            {
                string term = Regex.Unescape(searchTerms[i]);
                if (objectName.IndexOf(term, System.StringComparison.OrdinalIgnoreCase) < 0)
                    return false;
            }

            return true;
        }

        private static void SetDescendantFlags(TreeViewItem parent, bool shouldProcess)
        {
            if (parent.children == null)
            {
                return;
            }

            for (int i = 0; i < parent.children.Count; i++)
            {
                FbxMeshObjectTreeViewItem child = parent.children[i] as FbxMeshObjectTreeViewItem;
                if (child == null)
                {
                    continue;
                }

                child.Rule.ShouldProcess = shouldProcess;
                SetDescendantFlags(child, shouldProcess);
            }
        }

        private static bool HasMixedDescendantFlags(TreeViewItem parent)
        {
            if (parent.children == null || parent.children.Count == 0)
            {
                return false;
            }

            bool hasTrue = false;
            bool hasFalse = false;
            for (int i = 0; i < parent.children.Count; i++)
            {
                FbxMeshObjectTreeViewItem child = parent.children[i] as FbxMeshObjectTreeViewItem;
                if (child == null)
                {
                    continue;
                }

                if (child.Rule.ShouldProcess)
                {
                    hasTrue = true;
                }
                else
                {
                    hasFalse = true;
                }

                if (HasMixedDescendantFlags(child))
                {
                    return true;
                }
            }

            return hasTrue && hasFalse;
        }
    }

    internal sealed class FbxMeshObjectTreeViewItem : TreeViewItem
    {
        public readonly FbxMeshObjectProcessingRule Rule;

        public FbxMeshObjectTreeViewItem(int id, int depth, string displayName,
                                         FbxMeshObjectProcessingRule rule)
            : base(id, depth, displayName)
        {
            Rule = rule;
        }
    }
}
