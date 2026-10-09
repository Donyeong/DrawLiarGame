// =======================================================
// Text Animator for Unity - Copyright (c) 2018-Today, Febucci SRL, febucci.com
// - LICENSE: https://www.textanimatorforgames.com/legal/eula
// - DOCUMENTATION: https://docs.febucci.com/text-animator-unity/
// - WEBSITE: https://www.textanimatorforgames.com/
// =======================================================

using System.Collections.Generic;
using Febucci.TextAnimatorCore.Data;
using Febucci.TextAnimatorCore;
using Febucci.TextAnimatorForUnity.Effects;
using Febucci.TextAnimatorForUnity.Effects.Core;
using Febucci.TextAnimatorForUnity.Parsing;

namespace Febucci.TextAnimatorForUnity
{

    /// <summary>
    /// Contains animations that will be recognized and used by Text Animator
    /// </summary>
    [System.Serializable]
    [UnityEngine.CreateAssetMenu(fileName = "Animations Database", menuName = ScriptablePaths.EFFECT_STATES_PATH + "Create Animations Database", order = 100)]
    public class AnimationsDatabase : Database<EffectScriptableBase>, IDatabaseProvider<IEffect>
    {
        public override bool IsCaseSensitive => false;

        Dictionary<string, IEffect> converted;
        public Dictionary<string, IEffect> Database
        {
            get
            {
                BuildOnce();
                return converted;
            }
        }

        // fixes method not available in base class because of generic :C
        [UnityEngine.ContextMenu("Force rebuild")]
        public override void ForceBuildRefresh()
        {
            base.ForceBuildRefresh();
        }

        protected override void OnBuildOnce()
        {
            base.OnBuildOnce();
            converted = new Dictionary<string, IEffect>();
            foreach (var pair in base.Dictionary)
                converted.Add(pair.Key, pair.Value);
        }

        protected override bool IsTagAllowed(string tagId, EffectScriptableBase source)
        {
            string sourceTagId = source != null ? source.TagID : tagId;
            if (!UnityRichTextTagUtility.IsReservedTagName(sourceTagId))
                return true;

            UnityEngine.Debug.LogWarning($"Text Animator: {UnityRichTextTagUtility.GetReservedTagWarning(sourceTagId)} Skipping...", source);
            return false;
        }

    }
}
