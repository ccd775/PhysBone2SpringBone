using System;
using UnityEditor;

namespace ccd775.AvatarPhysBoneConverter
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
