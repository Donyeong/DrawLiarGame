// =======================================================
// Text Animator for Unity - Copyright (c) 2018-Today, Febucci SRL, febucci.com
// - LICENSE: https://www.textanimatorforgames.com/legal/eula
// - DOCUMENTATION: https://docs.febucci.com/text-animator-unity/
// - WEBSITE: https://www.textanimatorforgames.com/
// =======================================================

using UnityEngine;

// import Text Animator's namespaces
using Febucci.TextAnimatorCore;
using Febucci.TextAnimatorCore.Text;
using Febucci.Parsing;
using Febucci.TextAnimatorForUnity.Effects;
using Febucci.TextAnimatorForUnity;

namespace Febucci.Examples
{
    [System.Serializable]
    class ComboFlashingParameters
    {
        public float amount = 1.5f;
    }

    struct ComboFlashingState : IEffectState
    {
        readonly float defaultAmount;
        float amount;


        public ComboFlashingState(ComboFlashingParameters data)
        {
            this.defaultAmount = data.amount;
            this.amount = defaultAmount;
        }

        public void UpdateParameters(RegionParameters parameters)
        {
            amount = parameters.ModifyFloat("a", defaultAmount);
        }

        public void Apply(ref CharacterData character, in ManagedEffectContext context)
        {
            var saturation = context.progressionRange / 2.0f + 0.5f;
            character.SetColor(Color.HSVToRGB(0.1667f, saturation * amount, 1.0f));
        }
    }

    [System.Serializable]
    [CreateAssetMenu(menuName = ScriptablePaths.EFFECT_STATES_SPECIAL + "Combo Flashing", fileName = "ComboFlashingEffect")]
    class ComboFlashingScriptable : ManagedEffectScriptable<ComboFlashingState, ComboFlashingParameters>
    {
        // simply creates a new State, given the Parameters (already managed by text animator)
        protected override ComboFlashingState CreateState(ComboFlashingParameters parameters)
            => new ComboFlashingState(parameters);
    }
}
