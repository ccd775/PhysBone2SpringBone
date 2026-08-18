using System;
using System.Linq;
using UniVRM10;
using UnityEditor;
using UnityEngine;

namespace ccd775.AvatarPhysBoneConverter
{
    public sealed class PhysBone2SpringBoneWindow : EditorWindow
    {
        private Animator _root;
        private GameObject _exportTarget;
        private PhysBone2SpringBoneAnalysis _analysis;
        private Vector2 _windowScroll;
        private Vector2 _scroll;
        private bool _showDetails = true;
        private bool _deleteSourceComponents;
        private string _author = "Unknown";
        private string _outputFolder = "Assets/PhysBone2SpringBoneGenerated";
        private string _prefabFolder = "Assets/PhysBone2SpringBoneGenerated/Prefabs";
        private string _lastMessage;
        private MessageType _lastMessageType;

        [MenuItem("Tools/Avatar/PhysBone to VRM 1 SpringBone")]
        private static void Open()
        {
            ShowWindow();
        }

        public static void ShowWindow()
        {
            var window = GetWindow<PhysBone2SpringBoneWindow>();
            window.titleContent = new GUIContent("PhysBone → VRM1 SpringBone");
            window.minSize = new Vector2(520.0f, 480.0f);
        }

        private void OnGUI()
        {
            _windowScroll = EditorGUILayout.BeginScrollView(_windowScroll);
            EditorGUILayout.LabelField("VRC PhysBone → VRM 1.0 SpringBone", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "转换会保留 PhysBone 的实际初始化链、逐骨骼曲线、显式 Collider 引用，以及 Sphere/Capsule/Plane/Inside 形状。" +
                "两个求解器不能数学等价；分析报告会明确列出 VRM 1.0 无法表达的功能。",
                MessageType.Info);

            EditorGUI.BeginChangeCheck();
            _root = (Animator)EditorGUILayout.ObjectField("Avatar Animator", _root, typeof(Animator), true);
            if (EditorGUI.EndChangeCheck())
            {
                _analysis = null;
                _exportTarget = _root != null ? _root.gameObject : null;
                _lastMessage = null;
            }

            using (new EditorGUI.DisabledScope(_root == null))
            {
                if (GUILayout.Button("分析转换质量与兼容性", GUILayout.Height(28.0f)))
                {
                    RunAnalysis();
                }
            }

            DrawAnalysis();
            EditorGUILayout.Space(8.0f);
            DrawOptions();
            EditorGUILayout.Space(8.0f);
            DrawActions();
            EditorGUILayout.Space(8.0f);
            DrawExport();

            if (!string.IsNullOrEmpty(_lastMessage))
            {
                EditorGUILayout.Space(8.0f);
                EditorGUILayout.HelpBox(_lastMessage, _lastMessageType);
            }
            EditorGUILayout.EndScrollView();
        }

        private void DrawAnalysis()
        {
            if (_analysis == null)
            {
                return;
            }

            var summaryType = _analysis.ErrorCount > 0
                ? MessageType.Error
                : _analysis.WarningCount > 0 ? MessageType.Warning : MessageType.Info;
            EditorGUILayout.HelpBox(
                $"PhysBone {_analysis.PhysBoneCount} | Spring {_analysis.SpringCount} | 动态骨骼 {_analysis.JointCount} | 模拟段 {_analysis.SimulatedSegmentCount} | " +
                $"Collider {_analysis.SourceColliderCount} (Sphere {_analysis.SphereColliderCount}, Capsule {_analysis.CapsuleColliderCount}, Plane {_analysis.PlaneColliderCount}) | " +
                $"显式引用 {_analysis.ExplicitColliderReferenceCount} | 错误 {_analysis.ErrorCount} | 警告 {_analysis.WarningCount}",
                summaryType);

            if (_analysis.Issues.Count == 0)
            {
                return;
            }

            _showDetails = EditorGUILayout.Foldout(_showDetails, "逐项诊断", true);
            if (!_showDetails)
            {
                return;
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.MinHeight(130.0f), GUILayout.MaxHeight(260.0f));
            foreach (var issue in _analysis.Issues)
            {
                var type = issue.Severity == PhysBone2SpringBoneIssueSeverity.Error
                    ? MessageType.Error
                    : issue.Severity == PhysBone2SpringBoneIssueSeverity.Warning
                        ? MessageType.Warning
                        : MessageType.Info;
                EditorGUILayout.HelpBox(issue.ObjectPath + "\n" + issue.Message, type);
            }
            EditorGUILayout.EndScrollView();
        }

        private void DrawOptions()
        {
            EditorGUILayout.LabelField("转换选项", EditorStyles.boldLabel);
            _author = EditorGUILayout.TextField(new GUIContent("VRM Author", "用于新建 VRM10Object 的必填元数据。"), _author);
            _outputFolder = EditorGUILayout.TextField(new GUIContent("VRM10Object Folder", "新建 VRM10Object 资产的保存目录。"), _outputFolder);
            _deleteSourceComponents = EditorGUILayout.ToggleLeft(
                new GUIContent("转换成功后删除源 PhysBone / Collider（不可安全重建，不推荐）"),
                _deleteSourceComponents);

            if (_deleteSourceComponents)
            {
                EditorGUILayout.HelpBox("默认行为是保留并禁用源组件。删除后将失去原始曲线、权限、限制和抓取等数据。", MessageType.Error);
            }
        }

        private void DrawActions()
        {
            var canConvert = _root != null && _analysis != null && _analysis.CanConvert;
            using (new EditorGUI.DisabledScope(!canConvert))
            {
                if (GUILayout.Button("转换为 VRM 1.0 SpringBone", GUILayout.Height(34.0f)))
                {
                    if (_deleteSourceComponents && !EditorUtility.DisplayDialog(
                            "确认删除源数据",
                            "转换成功后将删除全部源 VRC PhysBone 和 PhysBoneCollider。该模式无法安全重建。是否继续？",
                            "删除并转换",
                            "取消"))
                    {
                        return;
                    }
                    RunConversion();
                }
            }

            var manifest = _root != null ? _root.GetComponent<PhysBone2SpringBoneManifest>() : null;
            var canRemoveGenerated = manifest != null && !manifest.SourceComponentsDeleted;
            using (new EditorGUI.DisabledScope(!canRemoveGenerated))
            {
                if (GUILayout.Button("移除本工具生成的数据并恢复源组件"))
                {
                    if (PhysBone2SpringBoneConverter.RemoveGenerated(_root))
                    {
                        _analysis = null;
                        SetMessage("已移除本工具生成的组件，并恢复源组件的原 enabled 状态。持久化 VRM10Object 资产予以保留。", MessageType.Info);
                    }
                }
            }
            if (manifest != null && manifest.SourceComponentsDeleted)
            {
                EditorGUILayout.HelpBox("源 PhysBone/Collider 已删除；为避免头像同时失去源物理和转换物理，无法再移除生成数据。", MessageType.Warning);
            }
        }

        private void DrawExport()
        {
            EditorGUILayout.LabelField("Prefab / VRM 1.0 导出", EditorStyles.boldLabel);
            _prefabFolder = EditorGUILayout.TextField(
                new GUIContent("Converted Prefab Folder", "转换后 Prefab 的保存目录。"),
                _prefabFolder);

            var convertedSceneRoot = _root != null && _root.GetComponent<PhysBone2SpringBoneManifest>() != null;
            using (new EditorGUI.DisabledScope(!convertedSceneRoot))
            {
                if (GUILayout.Button("保存转换后的 Prefab"))
                {
                    SaveConvertedPrefab();
                }
            }

            _exportTarget = (GameObject)EditorGUILayout.ObjectField(
                new GUIContent("VRM 1.0 Export Target", "可选择刚保存的 Prefab，或已经转换的场景头像。"),
                _exportTarget,
                typeof(GameObject),
                true);

            var canExport = _exportTarget != null &&
                            _exportTarget.TryGetComponent<Vrm10Instance>(out var instance) &&
                            instance.Vrm != null;
            using (new EditorGUI.DisabledScope(!canExport))
            {
                if (GUILayout.Button("打开 UniVRM 的 VRM 1.0 导出面板", GUILayout.Height(30.0f)))
                {
                    try
                    {
                        PhysBone2SpringBoneExportUtility.OpenVrm1Exporter(_exportTarget);
                        SetMessage("已把目标交给 UniVRM 导出面板；请确认 VRM Meta、Mesh 和 Export Settings 后导出 .vrm。", MessageType.Info);
                    }
                    catch (Exception ex)
                    {
                        SetMessage(ex.Message, MessageType.Error);
                        Debug.LogException(ex);
                    }
                }
            }
        }

        private void SaveConvertedPrefab()
        {
            try
            {
                var prefab = PhysBone2SpringBoneExportUtility.SaveConvertedPrefab(_root, _prefabFolder);
                _exportTarget = prefab;
                Selection.activeObject = prefab;
                SetMessage("转换后的 Prefab 已保存：" + AssetDatabase.GetAssetPath(prefab), MessageType.Info);
            }
            catch (Exception ex)
            {
                SetMessage(ex.Message, MessageType.Error);
                Debug.LogException(ex);
            }
        }

        private void RunAnalysis()
        {
            try
            {
                _analysis = PhysBone2SpringBoneConverter.Analyze(_root);
                SetMessage(
                    _analysis.CanConvert
                        ? "分析完成。转换前请审阅逐项警告。"
                        : "分析发现阻断错误，尚不能转换。",
                    _analysis.CanConvert ? MessageType.Info : MessageType.Error);
            }
            catch (Exception ex)
            {
                _analysis = null;
                SetMessage(ex.Message, MessageType.Error);
            }
        }

        private void RunConversion()
        {
            try
            {
                var result = PhysBone2SpringBoneConverter.Convert(
                    _root,
                    new PhysBone2SpringBoneConversionOptions
                    {
                        DeleteSourceComponents = _deleteSourceComponents,
                        CreatePersistentVrmObject = true,
                        VrmObjectFolder = _outputFolder,
                        Author = _author,
                    });
                _analysis = result.Analysis;
                Selection.activeObject = result.Instance;
                SetMessage(
                    "转换完成：源组件" + (_deleteSourceComponents ? "已删除" : "已保留并禁用") +
                    (string.IsNullOrEmpty(result.VrmObjectAssetPath) ? "。" : "；VRM10Object: " + result.VrmObjectAssetPath),
                    MessageType.Info);
            }
            catch (Exception ex)
            {
                SetMessage(ex.Message, MessageType.Error);
                Debug.LogException(ex);
            }
        }

        private void SetMessage(string message, MessageType type)
        {
            _lastMessage = message;
            _lastMessageType = type;
            Repaint();
        }
    }
}
