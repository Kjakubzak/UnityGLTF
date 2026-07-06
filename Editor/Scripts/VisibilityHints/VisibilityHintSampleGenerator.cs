#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityGLTF.VisibilityHints.Editor
{
    /// <summary>
    /// Editor menu that builds a small sample hierarchy exercising both view-context visibility-hint extensions
    /// and exports it to a <c>.glb</c> (with the VisibilityHints export plugin enabled on a fresh, isolated
    /// default-settings instance). The hierarchy is left in the scene so the inspectors and the Play-mode Mode toggle can be tried
    /// against it, and the exported file re-imports (with the import plugin enabled) with the components restored.
    /// </summary>
    public static class VisibilityHintSampleGenerator
    {
        // Placed just after the GLTFExportMenu GameObject export entries (priorities 34-35), before Settings (3000).
        [MenuItem("GameObject/UnityGLTF/Generate Visibility Hints Sample", false, 40)]
        public static void Generate(MenuCommand command)
        {
            // GameObject/ menu items are invoked once per selected object; this generator builds its own
            // hierarchy and ignores the selection, so run only once (mirrors the GLTFExportMenu guard).
            if (command.context && Selection.objects.Length > 1 && command.context != Selection.objects[0])
                return;

            var root = new GameObject("VisibilityHintsSample");
            Undo.RegisterCreatedObjectUndo(root, "Generate Visibility Hints Sample");

            // Head: a single-sub-mesh node, hinted third_person_only (hidden when the view context is first person).
            var head = MakeMeshChild(root, "Head", SingleTriangleMesh("HeadMesh"), NewMaterial("HeadMat"));
            head.transform.localPosition = new Vector3(0f, 1f, 0f);

            // Body: two sub-meshes; sub-mesh 1 is hinted first_person_only (e.g. arms visible only in first person).
            var bodyMesh = TwoSubMeshMesh("BodyMesh");
            MakeMeshChild(root, "Body", bodyMesh, NewMaterial("BodyMat0"), NewMaterial("BodyMat1"));

            root.AddComponent<ViewContextController>();

            root.AddComponent<NodeVisibilityHintSet>().Bind(new List<NodeVisibilityHintSet.NodeVisibilityEntry>
            {
                new NodeVisibilityHintSet.NodeVisibilityEntry
                {
                    Node = head.transform,
                    Role = VisibilityHintExtensionNames.RoleThirdPersonOnly,
                    Label = "Head",
                },
            });

            root.AddComponent<PrimitiveVisibilityHintSet>().Bind(new List<PrimitiveVisibilityHintSet.PrimitiveVisibilityEntry>
            {
                new PrimitiveVisibilityHintSet.PrimitiveVisibilityEntry
                {
                    Mesh = bodyMesh,
                    SubMesh = 1,
                    Role = VisibilityHintExtensionNames.RoleFirstPersonOnly,
                    Label = "BodyArms",
                },
            });

            Selection.activeGameObject = root;
            EditorGUIUtility.PingObject(root);

            var folder = EditorUtility.SaveFolderPanel("Export Visibility Hints Sample (.glb)", "", "");
            if (string.IsNullOrEmpty(folder)) return; // cancelled: the hierarchy stays in the scene for inspection

            // Fresh default settings so the project-wide export settings are untouched; enable just our plugin.
            var settings = GLTFSettings.GetDefaultSettings();
            bool pluginEnabled = false;
            foreach (var plugin in settings.ExportPlugins)
                if (plugin is VisibilityHintExportPlugin) { plugin.Enabled = true; pluginEnabled = true; }
            if (!pluginEnabled)
                Debug.LogWarning("[VisibilityHints] VisibilityHintExportPlugin was not found; the exported sample will omit visibility hints.");

            var exporter = new GLTFSceneExporter(new[] { root.transform }, new ExportContext(settings));
            exporter.SaveGLB(folder, root.name);

            var file = GLTFSceneExporter.GetFileName(folder, root.name, ".glb");
            Debug.Log($"[VisibilityHints] Exported sample to {file}");
            EditorUtility.RevealInFinder(file);
        }

        private static GameObject MakeMeshChild(GameObject parent, string name, Mesh mesh, params Material[] materials)
        {
            var go = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
            go.transform.SetParent(parent.transform, false);
            go.GetComponent<MeshFilter>().sharedMesh = mesh;
            go.GetComponent<MeshRenderer>().sharedMaterials = materials; // one material per sub-mesh (non-null)
            return go;
        }

        private static Mesh SingleTriangleMesh(string name)
        {
            var mesh = new Mesh { name = name };
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
            mesh.triangles = new[] { 0, 1, 2 };
            mesh.RecalculateNormals();
            Undo.RegisterCreatedObjectUndo(mesh, "Generate Visibility Hints Sample");
            return mesh;
        }

        // Two disjoint triangles as two separate sub-meshes, so a per-primitive hint can target sub-mesh 1.
        private static Mesh TwoSubMeshMesh(string name)
        {
            var mesh = new Mesh { name = name };
            mesh.vertices = new[]
            {
                Vector3.zero, Vector3.right, Vector3.up,                              // sub-mesh 0
                new Vector3(1f, 0f, 0f), new Vector3(2f, 0f, 0f), new Vector3(1f, 1f, 0f), // sub-mesh 1
            };
            mesh.subMeshCount = 2;
            mesh.SetTriangles(new[] { 0, 1, 2 }, 0);
            mesh.SetTriangles(new[] { 3, 4, 5 }, 1);
            mesh.RecalculateNormals();
            Undo.RegisterCreatedObjectUndo(mesh, "Generate Visibility Hints Sample");
            return mesh;
        }

        // A null material makes the exporter skip a sub-mesh, so every slot needs a real material.
        private static Material NewMaterial(string name)
        {
            var shader = Shader.Find("Unlit/Color") ?? Shader.Find("Sprites/Default")
                         ?? Shader.Find("Standard") ?? Shader.Find("Hidden/InternalErrorShader");
            var material = new Material(shader) { name = name };
            Undo.RegisterCreatedObjectUndo(material, "Generate Visibility Hints Sample");
            return material;
        }
    }
}
#endif
