// =======================================================
// Text Animator for Unity - Copyright (c) 2018-Today, Febucci SRL, febucci.com
// - LICENSE: https://www.textanimatorforgames.com/legal/eula
// - DOCUMENTATION: https://docs.febucci.com/text-animator-unity/
// - WEBSITE: https://www.textanimatorforgames.com/
// =======================================================

using Febucci.TextAnimatorForUnity;
using UnityEngine;

namespace Febucci.Examples
{
    //Prevents this example to show up in the inspector, since it should be used only in the example scene (and so, not annoy you after you understand how this works)
    [AddComponentMenu("")]
    public class CurvesAndPlaybacksFormatter : MonoBehaviour
    {
        public GameObject pressPlay;
        public TypewriterComponent headerTypewriter;
        public TypewriterComponent curvesTypewriter;
        public TypewriterComponent playbacksTypewriter;

        [TextArea(3, 50), SerializeField]
        string headerTextToShow = " ";
        [TextArea(3, 50), SerializeField]
        string curvesTextToShow = " ";
        [TextArea(3, 50), SerializeField]
        string playbacksTextToShow = " ";

        private void Awake()
        {
            UnityEngine.Assertions.Assert.IsNotNull(pressPlay, $"PressPlay object is null in {gameObject.name}");
            UnityEngine.Assertions.Assert.IsNotNull(headerTypewriter, $"Header Animator Player component is null in {gameObject.name}");
            UnityEngine.Assertions.Assert.IsNotNull(curvesTypewriter, $"Curves Animator Player component is null in {gameObject.name}");
            UnityEngine.Assertions.Assert.IsNotNull(playbacksTypewriter, $"Playbacks Animator Player component is null in {gameObject.name}");
        }

        private void Start()
        {
            pressPlay.SetActive(false);
            headerTypewriter.onTextShowed.AddListener(ShowCurves);
            curvesTypewriter.onTextShowed.AddListener(ShowPlaybacks);
            ShowHeader();
        }

        void ShowHeader()
        {
            headerTypewriter.ShowText(headerTextToShow);
        }

        void ShowCurves()
        {
            curvesTypewriter.ShowText(curvesTextToShow);
        }

        void ShowPlaybacks()
        {
            playbacksTypewriter.ShowText(playbacksTextToShow);
        }
    }
}