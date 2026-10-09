// =======================================================
// Text Animator for Unity - Copyright (c) 2018-Today, Febucci SRL, febucci.com
// - LICENSE: https://www.textanimatorforgames.com/legal/eula
// - DOCUMENTATION: https://docs.febucci.com/text-animator-unity/
// - WEBSITE: https://www.textanimatorforgames.com/
// =======================================================

using Febucci.Examples;
using UnityEngine;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Utilities;
#endif

/// <summary>
/// Connects player attacks to combo feedback, choosing visible attack variations and resetting after a pause.
/// </summary>
public class ComboMeterCombat : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Animator controlling the player character")]
    [SerializeField] Animator playerAnimator;
    [Tooltip("Button that triggers an attack")]
    [SerializeField] Button button;
    [Tooltip("Combo meter tracking hit count and text display")]
    [SerializeField] ComboMeter comboMeter;
    [Tooltip("Camera shake effect played on each attack")]
    [SerializeField] CameraShake cameraShake;

    [Header("Animations")]
    [Tooltip("Attack animation clips, one is picked randomly on each hit")]
    [SerializeField] AnimationClip[] attackClips;
    [Tooltip("Idle animation clip played when the combo resets")]
    [SerializeField] AnimationClip idleClip;

    [Header("Combo Settings")]
    [Tooltip("Time in seconds before the combo resets after the last hit")]
    [SerializeField] float comboResetTime = 3f;

    // State
    float timeSinceLastHit;
    bool comboActive;
    int lastAttackClipIndex = -1;
    AnimatorOverrideController overrideController;

    // Animator constants
    const string STATE_NAME = "HeroIdle";
    const int LAYER_INDEX = 0;
    const float NORMALIZED_TIME = 0f;

    bool pressed;

    void Start()
    {
        button.onClick.AddListener(Attack);

        // Create an override controller to swap clips at runtime.
        overrideController = new AnimatorOverrideController(playerAnimator.runtimeAnimatorController);
        playerAnimator.runtimeAnimatorController = overrideController;

#if ENABLE_INPUT_SYSTEM
        InputSystem.onAnyButtonPress.Call(OnAnyInputPressed);
#endif
    }

#if ENABLE_INPUT_SYSTEM
    void OnAnyInputPressed(InputControl control)
    {
        if (Mouse.current != null && control.device == Mouse.current)
            return; // Skip mouse input because the UI button handles clicks.

        pressed = true;
    }
#endif

    void Attack()
    {
        comboMeter.IncreaseCounter();
        cameraShake.Play();

        // Pick a random attack while avoiding the same sprite twice in a row.
        int clipIndex = Random.Range(0, attackClips.Length);
        if (attackClips.Length > 1 && clipIndex == lastAttackClipIndex)
            clipIndex = (clipIndex + 1) % attackClips.Length;

        lastAttackClipIndex = clipIndex;
        AnimationClip clip = attackClips[clipIndex];
        overrideController[idleClip] = clip;
        playerAnimator.Play(STATE_NAME, LAYER_INDEX, NORMALIZED_TIME);

        timeSinceLastHit = 0f;
        comboActive = true;
    }

    void Update()
    {
#if ENABLE_LEGACY_INPUT_MANAGER
        pressed |= Input.GetKeyDown(KeyCode.Space);
#endif

        if (pressed)
        {
            Attack();
        }

        pressed = false;

        if (!comboActive) return;

        // Track how long the combo has been idle.
        timeSinceLastHit += Time.deltaTime;
        if (timeSinceLastHit >= comboResetTime)
        {
            comboActive = false;
            comboMeter.ResetCombo();

            // Restore the idle clip after the combo ends.
            overrideController[idleClip] = idleClip;
            playerAnimator.Play(STATE_NAME, LAYER_INDEX, NORMALIZED_TIME);
        }
    }
}
