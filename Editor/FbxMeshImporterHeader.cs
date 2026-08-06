using System;
using UnityEditor;
using UnityEngine;

namespace Core
{
    [InitializeOnLoad]
    internal static class FbxMeshImporterHeader
    {
        static FbxMeshImporterHeader()
        {
            UnityEditor.Editor.finishedDefaultHeaderGUI += DrawHeaderButton;
        }

        private static void DrawHeaderButton(UnityEditor.Editor editor)
        {
            if (editor == null || editor.target == null)
            {
                return;
            }

            if (editor.target as ModelImporter == null)
            {
                return;
            }

            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            GUIContent icon = EditorGUIUtility.IconContent("_Popup", "Remove Mesh Duplicates Settings");
            if (GUILayout.Button(icon, EditorStyles.iconButton, GUILayout.Width(22f), GUILayout.Height(18f)))
            {
                FbxMeshImportWindow.OpenForAsset(editor.target);
            }

            GUILayout.EndHorizontal();
        }
    }
}