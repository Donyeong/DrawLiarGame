using System;
using System.Collections.Generic;
using UnityEngine;

namespace DrawLiar
{
    public enum DrawSound { UiClick, UiConfirm, UiCancel, UiError, TurnStart, PhaseChange, Countdown, RoundResult, PlayerJoined, Chat }

    [DisallowMultipleComponent]
    public sealed class DrawAudio : MonoBehaviour
    {
        private const string RESOURCE_PATH = "DrawLiar/Audio/";
        private const string MASTER_KEY = "DrawLiar.Audio.Master";
        private const string MUSIC_KEY = "DrawLiar.Audio.Music";
        private const string EFFECTS_KEY = "DrawLiar.Audio.Effects";
        private const string MUTED_KEY = "DrawLiar.Audio.Muted";
        private const int EFFECT_SOURCE_COUNT = 3;
        private const float MUSIC_FADE_SECONDS = .6f;
        private static readonly float[] CUE_GAINS = { .6f, .55f, .55f, .55f, .45f, .45f, .45f, .45f, .35f, .3f };
        private static readonly float[] CUE_COOLDOWNS = { .065f, .15f, .15f, .5f, .5f, .5f, .4f, 1f, .6f, .25f };
        private static readonly int[] CUE_PRIORITIES = { 2, 4, 3, 5, 8, 7, 6, 9, 1, 0 };

        private readonly HashSet<int> _connectedPlayers = new HashSet<int>();
        private readonly HashSet<int> _currentPlayers = new HashSet<int>();
        private AudioSource[] _musicSources;
        private AudioSource[] _effectSources;
        private AudioClip[] _effectClips;
        private AudioClip _lobbyMusic, _gameMusic;
        private float[] _lastPlayed, _effectStarted, _effectGains, _musicGains;
        private int[] _effectPriorities;
        private bool[] _musicStarted;
        private DrawNetworkManager _network;
        private AudioListener _ownedListener;
        private GamePhase _phase;
        private int _round, _artistId = -1, _localPlayerId = -1, _lastCountdown = int.MaxValue, _lastResultRound = -1, _targetMusic = -1;
        private bool _hasSnapshot, _focused = true, _paused, _duplicate, _settingsDirty;
        private float _masterVolume = .8f, _musicVolume = .32f, _effectsVolume = .65f;

        public static DrawAudio Instance { get; private set; }
        public float MasterVolume => _masterVolume;
        public float MusicVolume => _musicVolume;
        public float EffectsVolume => _effectsVolume;
        public bool Muted { get; private set; }
        private bool IsSuspended => !_focused || _paused || !isActiveAndEnabled;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetInstance() => Instance = null;

        private void Awake()
        {
#if UNITY_SERVER
            enabled = false;
            return;
#else
            if (Instance != null && Instance != this) { _duplicate = true; Destroy(this); return; }
            Instance = this;
            _network = GetComponent<DrawNetworkManager>();
            _masterVolume = ValidVolume(PlayerPrefs.GetFloat(MASTER_KEY, .8f), .8f);
            _musicVolume = ValidVolume(PlayerPrefs.GetFloat(MUSIC_KEY, .32f), .32f);
            _effectsVolume = ValidVolume(PlayerPrefs.GetFloat(EFFECTS_KEY, .65f), .65f);
            Muted = PlayerPrefs.GetInt(MUTED_KEY, 0) != 0;
            _focused = Application.isFocused;
            EnsureListener();
            _lobbyMusic = Resources.Load<AudioClip>(RESOURCE_PATH + "LobbyMusic");
            _gameMusic = Resources.Load<AudioClip>(RESOURCE_PATH + "GameMusic");
            _musicSources = new AudioSource[2];
            _musicGains = new float[2];
            _musicStarted = new bool[2];
            for (int index = 0; index < _musicSources.Length; index++)
                _musicSources[index] = CreateSource("Music " + (index + 1), true);
            int cueCount = Enum.GetValues(typeof(DrawSound)).Length;
            _effectClips = new AudioClip[cueCount];
            _lastPlayed = new float[cueCount];
            for (int index = 0; index < cueCount; index++)
            {
                _effectClips[index] = Resources.Load<AudioClip>(RESOURCE_PATH + ((DrawSound)index));
                _lastPlayed[index] = float.NegativeInfinity;
            }
            _effectSources = new AudioSource[EFFECT_SOURCE_COUNT];
            _effectStarted = new float[EFFECT_SOURCE_COUNT];
            _effectGains = new float[EFFECT_SOURCE_COUNT];
            _effectPriorities = new int[EFFECT_SOURCE_COUNT];
            for (int index = 0; index < EFFECT_SOURCE_COUNT; index++)
                _effectSources[index] = CreateSource("Effect " + (index + 1), false);
            SwitchMusic(_lobbyMusic);
#endif
        }

        private void EnsureListener()
        {
            foreach (var listener in FindObjectsByType<AudioListener>(FindObjectsSortMode.None))
                if (listener.isActiveAndEnabled) return;
            _ownedListener = gameObject.AddComponent<AudioListener>();
        }

        private AudioSource CreateSource(string name, bool music)
        {
            var source = new GameObject(name, typeof(AudioSource)).GetComponent<AudioSource>();
            source.transform.SetParent(transform, false);
            source.playOnAwake = false;
            source.loop = music;
            source.spatialBlend = 0f;
            source.dopplerLevel = 0f;
            source.priority = music ? 128 : 96;
            source.volume = 0f;
            return source;
        }

        private void OnEnable()
        {
            if (_duplicate || _network == null || _musicSources == null) return;
            _network.StateChanged += OnStateChanged;
            _network.ChatReceived += OnChatReceived;
            _hasSnapshot = false;
            OnStateChanged(_network.State);
            UpdateSuspension();
        }

        private void OnDisable()
        {
            if (_network != null)
            {
                _network.StateChanged -= OnStateChanged;
                _network.ChatReceived -= OnChatReceived;
            }
            UpdateSuspension();
            FlushSettings();
        }

        public void Play(DrawSound sound)
        {
            int cue = (int)sound;
            if (_effectSources == null || cue < 0 || cue >= _effectClips.Length || IsSuspended || Muted || _masterVolume <= 0f || _effectsVolume <= 0f) return;
            var clip = _effectClips[cue];
            float now = Time.unscaledTime;
            if (clip == null || now - _lastPlayed[cue] < CUE_COOLDOWNS[cue]) return;
            int available = -1;
            for (int index = 0; index < _effectSources.Length; index++)
            {
                if (!_effectSources[index].isPlaying) { available = index; break; }
                if (_effectPriorities[index] <= CUE_PRIORITIES[cue] &&
                    (available < 0 || _effectPriorities[index] < _effectPriorities[available] ||
                    (_effectPriorities[index] == _effectPriorities[available] && _effectStarted[index] < _effectStarted[available]))) available = index;
            }
            if (available < 0) return;
            var source = _effectSources[available];
            source.Stop();
            source.clip = clip;
            _effectPriorities[available] = CUE_PRIORITIES[cue];
            _effectStarted[available] = now;
            _effectGains[available] = CUE_GAINS[cue];
            source.volume = _masterVolume * _effectsVolume * _effectGains[available];
            source.Play();
            _lastPlayed[cue] = now;
        }

        public void SetVolumes(float master, float music, float effects)
        {
            master = ValidVolume(master, _masterVolume);
            music = ValidVolume(music, _musicVolume);
            effects = ValidVolume(effects, _effectsVolume);
            if (_masterVolume == master && _musicVolume == music && _effectsVolume == effects) return;
            _masterVolume = master; _musicVolume = music; _effectsVolume = effects;
            PlayerPrefs.SetFloat(MASTER_KEY, master);
            PlayerPrefs.SetFloat(MUSIC_KEY, music);
            PlayerPrefs.SetFloat(EFFECTS_KEY, effects);
            _settingsDirty = true;
            ApplyVolumes();
        }

        public void SetMuted(bool muted)
        {
            if (Muted == muted) return;
            Muted = muted;
            PlayerPrefs.SetInt(MUTED_KEY, muted ? 1 : 0);
            _settingsDirty = true;
            ApplyVolumes();
        }

        public void FlushSettings()
        {
            if (!_settingsDirty) return;
            PlayerPrefs.Save();
            _settingsDirty = false;
        }

        private static float ValidVolume(float value, float fallback) => float.IsNaN(value) || float.IsInfinity(value) ? fallback : Mathf.Clamp01(value);

        private void ApplyVolumes()
        {
            float master = Muted ? 0f : _masterVolume;
            if (_musicSources != null)
                for (int index = 0; index < _musicSources.Length; index++)
                    _musicSources[index].volume = master * _musicVolume * _musicGains[index];
            if (_effectSources != null)
                for (int index = 0; index < _effectSources.Length; index++)
                    _effectSources[index].volume = master * _effectsVolume * _effectGains[index];
        }

        private void OnStateChanged(RoomSnapshot state)
        {
            if (state == null)
            {
                _hasSnapshot = false;
                _localPlayerId = -1;
                _lastCountdown = int.MaxValue;
                _lastResultRound = -1;
                _connectedPlayers.Clear();
                SwitchMusic(_lobbyMusic);
                return;
            }
            bool baseline = !_hasSnapshot;
            bool phaseChanged = !baseline && (_phase != state.Phase || _round != state.Round);
            bool turnChanged = !baseline && (phaseChanged || _artistId != state.ArtistId);
            if (state.Phase == GamePhase.Lobby) _lastResultRound = -1;
            else if (baseline && (state.Phase == GamePhase.RoundResults || state.Phase == GamePhase.MatchResults)) _lastResultRound = state.Round;
            SwitchMusic(state.Phase == GamePhase.Lobby || state.Phase == GamePhase.RoundResults || state.Phase == GamePhase.MatchResults ? _lobbyMusic : _gameMusic);
            if (phaseChanged)
            {
                if (state.Phase == GamePhase.RoundResults || state.Phase == GamePhase.MatchResults)
                {
                    if (_lastResultRound != state.Round) { Play(DrawSound.RoundResult); _lastResultRound = state.Round; }
                }
                else if (state.Phase != GamePhase.Lobby && !(state.Phase == GamePhase.Drawing && state.ArtistId == state.LocalPlayerId && !state.LocalIsSpectator)) Play(DrawSound.PhaseChange);
            }
            if (turnChanged && state.Phase == GamePhase.Drawing && state.ArtistId == state.LocalPlayerId && !state.LocalIsSpectator) Play(DrawSound.TurnStart);
            _currentPlayers.Clear();
            if (state.Players != null)
                foreach (var player in state.Players)
                    if (player != null && player.IsConnected) _currentPlayers.Add(player.Id);
            if (!baseline)
                foreach (int id in _currentPlayers)
                    if (id != state.LocalPlayerId && !_connectedPlayers.Contains(id)) { Play(DrawSound.PlayerJoined); break; }
            _connectedPlayers.Clear();
            _connectedPlayers.UnionWith(_currentPlayers);
            int seconds = Mathf.CeilToInt(state.RemainingSeconds);
            if (baseline) _lastCountdown = seconds;
            else if (phaseChanged || (state.Phase == GamePhase.Drawing && _artistId != state.ArtistId)) _lastCountdown = int.MaxValue;
            if (!baseline && IsCountdownPhase(state.Phase) && seconds >= 1 && seconds <= 5 && seconds < _lastCountdown)
            {
                Play(DrawSound.Countdown);
                _lastCountdown = seconds;
            }
            _phase = state.Phase; _round = state.Round; _artistId = state.ArtistId; _localPlayerId = state.LocalPlayerId;
            _hasSnapshot = true;
        }

        private static bool IsCountdownPhase(GamePhase phase) => phase == GamePhase.Drawing || phase == GamePhase.Discussion || phase == GamePhase.Rebuttal || phase == GamePhase.Voting || phase == GamePhase.Guessing;

        private void OnChatReceived(ChatLine line)
        {
            if (_hasSnapshot && line.PlayerId >= 0 && line.PlayerId != _localPlayerId && !string.IsNullOrWhiteSpace(line.Text)) Play(DrawSound.Chat);
        }

        private void SwitchMusic(AudioClip clip)
        {
            if (_musicSources == null) return;
            clip = clip != null ? clip : _lobbyMusic;
            if (clip == null || (_targetMusic >= 0 && _musicSources[_targetMusic].clip == clip)) return;
            int next = _targetMusic < 0 ? 0 : 1 - _targetMusic;
            var source = _musicSources[next];
            source.Stop();
            source.clip = clip;
            _musicGains[next] = 0f;
            source.volume = 0f;
            _musicStarted[next] = false;
            _targetMusic = next;
            if (!IsSuspended) { source.Play(); _musicStarted[next] = true; }
        }

        private void Update()
        {
            if (_musicSources == null || IsSuspended) return;
            float step = Time.unscaledDeltaTime / MUSIC_FADE_SECONDS;
            for (int index = 0; index < _musicSources.Length; index++)
            {
                _musicGains[index] = Mathf.MoveTowards(_musicGains[index], index == _targetMusic ? 1f : 0f, step);
                if (index != _targetMusic && _musicGains[index] <= 0f && _musicStarted[index])
                {
                    _musicSources[index].Stop();
                    _musicStarted[index] = false;
                }
            }
            ApplyVolumes();
        }

        private void UpdateSuspension()
        {
            if (_musicSources == null) return;
            if (IsSuspended)
            {
                foreach (var source in _musicSources) source.Pause();
                foreach (var source in _effectSources) source.Stop();
                return;
            }
            for (int index = 0; index < _musicSources.Length; index++)
            {
                if (_musicStarted[index]) _musicSources[index].UnPause();
                else if (index == _targetMusic && _musicSources[index].clip != null)
                {
                    _musicSources[index].Play();
                    _musicStarted[index] = true;
                }
            }
        }

        private void OnApplicationFocus(bool focused)
        {
            _focused = focused;
            UpdateSuspension();
            if (!focused) FlushSettings();
        }

        private void OnApplicationPause(bool paused)
        {
            _paused = paused;
            UpdateSuspension();
            if (paused) FlushSettings();
        }

        private void OnApplicationQuit() => FlushSettings();

        private void OnDestroy()
        {
            FlushSettings();
            if (_musicSources != null)
                foreach (var source in _musicSources) if (source != null) { source.Stop(); Destroy(source.gameObject); }
            if (_effectSources != null)
                foreach (var source in _effectSources) if (source != null) { source.Stop(); Destroy(source.gameObject); }
            if (_ownedListener != null) Destroy(_ownedListener);
            if (Instance == this) Instance = null;
        }
    }
}
