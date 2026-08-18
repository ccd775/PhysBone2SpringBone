using System.Collections.Generic;
using UnityEngine;

namespace sayunana
{
    [AddComponentMenu("")]
    [DisallowMultipleComponent]
    public sealed class PhysBone2SpringBoneManifest : MonoBehaviour
    {
        [HideInInspector] public string ConverterVersion;
        [HideInInspector] public string ConversionId;
        [HideInInspector] public Component Vrm10Instance;
        [HideInInspector] public ScriptableObject GeneratedVrmObject;
        [HideInInspector] public bool GeneratedVrm10Instance;
        [HideInInspector] public bool GeneratedHumanoid;
        [HideInInspector] public bool SourceComponentsDeleted;
        [HideInInspector] public List<Component> GeneratedComponents = new List<Component>();
        [HideInInspector] public List<GameObject> GeneratedObjects = new List<GameObject>();
        [HideInInspector] public List<Behaviour> SourceComponents = new List<Behaviour>();
        [HideInInspector] public List<bool> SourceEnabledStates = new List<bool>();
        [HideInInspector] public List<bool> SourceEnabledWasPrefabOverride = new List<bool>();
    }
}
