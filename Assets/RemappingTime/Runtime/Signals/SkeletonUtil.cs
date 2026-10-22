using UnityEngine;

namespace RemappingTime
{
    /// <summary>Helpers for reading joints from an <see cref="OVRSkeleton"/>.</summary>
    public static class SkeletonUtil
    {
        /// <summary>
        /// Returns the transform of the first bone matching any of <paramref name="ids"/>. Pass both the OVR
        /// and the OpenXR id of a joint (e.g. <c>Hand_IndexTip</c> and <c>XRHand_IndexTip</c>) so the code works
        /// with either skeleton type.
        /// </summary>
        public static Transform FindBone(OVRSkeleton skeleton, params OVRSkeleton.BoneId[] ids)
        {
            if (skeleton == null || skeleton.Bones == null) return null;
            foreach (OVRBone b in skeleton.Bones)
                foreach (OVRSkeleton.BoneId id in ids)
                    if (b.Id == id) return b.Transform;
            return null;
        }

        public static bool IsTracked(OVRHand hand, OVRSkeleton skeleton)
            => hand != null && skeleton != null && hand.IsTracked && skeleton.IsDataValid;

        public static Transform IndexTip(OVRSkeleton s)
            => FindBone(s, OVRSkeleton.BoneId.Hand_IndexTip, OVRSkeleton.BoneId.XRHand_IndexTip);

        public static Transform ThumbTip(OVRSkeleton s)
            => FindBone(s, OVRSkeleton.BoneId.Hand_ThumbTip, OVRSkeleton.BoneId.XRHand_ThumbTip);

        public static Transform Wrist(OVRSkeleton s)
            => FindBone(s, OVRSkeleton.BoneId.XRHand_Wrist, OVRSkeleton.BoneId.Hand_WristRoot);

        public static Transform IndexKnuckle(OVRSkeleton s)
            => FindBone(s, OVRSkeleton.BoneId.Hand_Index1, OVRSkeleton.BoneId.XRHand_IndexProximal);

        public static Transform LittleKnuckle(OVRSkeleton s)
            => FindBone(s, OVRSkeleton.BoneId.Hand_Pinky1, OVRSkeleton.BoneId.XRHand_LittleProximal);
    }
}
