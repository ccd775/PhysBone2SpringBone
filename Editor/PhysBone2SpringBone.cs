using System;
using UnityEditor;

namespace sayunana
{
    [Obsolete("Use PhysBone2SpringBoneWindow instead.")]
    public class PhysBone2SpringBone : EditorWindow
    {
        private void OnEnable()
        {
            PhysBone2SpringBoneWindow.ShowWindow();
            Close();
        }
    }
}
