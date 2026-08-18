using System;
using System.IO;
using System.Linq;
using UniVRM10;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ccd775.AvatarPhysBoneConverter
{
    public static class PhysBone2SpringBoneExportUtility
    {
        public static GameObject SaveConvertedPrefab(Animator root, string outputFolder)
        {
            if (Application.isPlaying)
            {
                throw new InvalidOperationException("A converted Prefab can only be saved in Edit Mode.");
            }
            if (root == null)
            {
                throw new ArgumentNullException(nameof(root), "Select an avatar Animator first.");
            }
            if (EditorUtility.IsPersistent(root))
            {
                throw new InvalidOperationException("The selected Animator is already a project asset. Select its scene instance to save a converted Prefab.");
            }
            var manifest = root.GetComponent<PhysBone2SpringBoneManifest>();
            if (manifest == null ||
                !root.TryGetComponent<Vrm10Instance>(out var instance) ||
                instance.Vrm == null)
            {
                throw new InvalidOperationException("Convert this avatar to VRM 1.0 SpringBone before saving its Prefab.");
            }

            var folder = NormalizeAssetFolder(outputFolder);
            EnsureAssetFolder(folder);
            var baseName = SanitizeFileName(root.name) + "_VRM1";
            var prefabPath = AssetDatabase.GenerateUniqueAssetPath(folder + "/" + baseName + ".prefab");
            var vrmObjectPath = AssetDatabase.GenerateUniqueAssetPath(folder + "/" + baseName + "_VRM10.asset");

            Scene previewScene = default;
            GameObject clone = null;
            VRM10Object clonedVrmObject = null;
            try
            {
                previewScene = EditorSceneManager.NewPreviewScene();
                clone = UnityEngine.Object.Instantiate(root.gameObject);
                clone.name = root.gameObject.name;
                SceneManager.MoveGameObjectToScene(clone, previewScene);
                if (PrefabUtility.IsPartOfPrefabInstance(clone))
                {
                    PrefabUtility.UnpackPrefabInstance(
                        PrefabUtility.GetOutermostPrefabInstanceRoot(clone),
                        PrefabUnpackMode.Completely,
                        InteractionMode.AutomatedAction);
                }

                var clonedManifest = clone.GetComponent<PhysBone2SpringBoneManifest>();
                var clonedInstance = clone.GetComponent<Vrm10Instance>();
                if (clonedManifest == null || clonedInstance == null)
                {
                    throw new InvalidOperationException("The cloned avatar lost its conversion manifest or Vrm10Instance.");
                }

                clonedVrmObject = UnityEngine.Object.Instantiate(instance.Vrm);
                clonedVrmObject.name = baseName + "_VRM10";
                clonedVrmObject.hideFlags = HideFlags.None;
                AssetDatabase.CreateAsset(clonedVrmObject, vrmObjectPath);
                clonedInstance.Vrm = clonedVrmObject;
                clonedManifest.Vrm10Instance = clonedInstance;
                clonedManifest.GeneratedVrmObject = clonedVrmObject;
                clonedManifest.SourceEnabledWasPrefabOverride = Enumerable
                    .Repeat(true, clonedManifest.SourceComponents.Count)
                    .ToList();

                var prefab = PrefabUtility.SaveAsPrefabAsset(clone, prefabPath, out var success);
                if (!success || prefab == null)
                {
                    throw new InvalidOperationException("Unity failed to save the converted Prefab.");
                }

                clonedVrmObject.Prefab = prefab;
                EditorUtility.SetDirty(clonedVrmObject);
                AssetDatabase.SaveAssetIfDirty(clonedVrmObject);
                return prefab;
            }
            catch
            {
                if (AssetDatabase.LoadMainAssetAtPath(prefabPath) != null)
                {
                    AssetDatabase.DeleteAsset(prefabPath);
                }
                if (AssetDatabase.LoadMainAssetAtPath(vrmObjectPath) != null)
                {
                    AssetDatabase.DeleteAsset(vrmObjectPath);
                }
                throw;
            }
            finally
            {
                if (clone != null)
                {
                    UnityEngine.Object.DestroyImmediate(clone);
                }
                if (previewScene.IsValid())
                {
                    EditorSceneManager.ClosePreviewScene(previewScene);
                }
            }
        }

        public static void OpenVrm1Exporter(GameObject target)
        {
            if (Application.isPlaying)
            {
                throw new InvalidOperationException("VRM 1.0 export is only supported in Edit Mode.");
            }
            if (target == null)
            {
                throw new ArgumentNullException(nameof(target), "Select a converted Prefab or scene avatar first.");
            }
            if (!target.TryGetComponent<Vrm10Instance>(out var instance) || instance.Vrm == null)
            {
                throw new InvalidOperationException("The export target requires a Vrm10Instance with VRM metadata.");
            }

            foreach (var window in Resources.FindObjectsOfTypeAll<VRM10ExportDialog>())
            {
                window.Close();
            }

            Selection.activeObject = target;
            EditorApplication.delayCall += VRM10ExportDialog.Open;
        }

        private static string NormalizeAssetFolder(string folder)
        {
            folder = string.IsNullOrWhiteSpace(folder)
                ? "Assets/PhysBone2SpringBoneGenerated/Prefabs"
                : folder.Replace('\\', '/').TrimEnd('/');
            if (folder != "Assets" && !folder.StartsWith("Assets/", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Prefab output folder must be inside Assets.");
            }
            return folder;
        }

        private static void EnsureAssetFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder))
            {
                return;
            }

            var parts = folder.Split('/');
            var current = parts[0];
            for (var i = 1; i < parts.Length; ++i)
            {
                var next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    AssetDatabase.CreateFolder(current, parts[i]);
                }
                current = next;
            }
        }

        private static string SanitizeFileName(string value)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var chars = (string.IsNullOrWhiteSpace(value) ? "Avatar" : value)
                .Select(x => invalid.Contains(x) ? '_' : x)
                .ToArray();
            return new string(chars);
        }
    }
}
