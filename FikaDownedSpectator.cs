using System;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using EFT.CameraControl;
using EFT.UI;
using Fika.Core.Main.FreeCamera;
using Fika.Core.Main.Players;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FikaDownedSpectator
{
    [BepInPlugin("com.zephyros.fikadownedspectator", "Fika Downed Spectator", "1.0.0")]
    [BepInDependency("com.fika.core")]
    public class FikaDownedSpectatorPlugin : BaseUnityPlugin
    {
        public static FikaDownedSpectatorPlugin Instance { get; private set; }

        public ConfigEntry<KeyboardShortcut> ToggleKey;
        public ConfigEntry<bool> ShowOverlayInfo;
        public ConfigEntry<string> UIPosition;
        public ConfigEntry<float> VerticalOffset;

        private bool _isSpectatingWhileDowned = false;

        private MethodInfo _setFirstPersonMethod;
        private MethodInfo _getPlayerMethod;
        private FieldInfo _deathFadeField;

        private PlayerPlateUI _cachedPlate;

        // Native Tarkov TextMeshPro UI Elements
        private GameObject _uiRoot;
        private RectTransform _uiRect;
        private RectTransform _textRect;
        private Image _bgImage;
        private TextMeshProUGUI _tmpText;
        private TMP_FontAsset _cachedBenderFont;

        private void Awake()
        {
            Instance = this;

            ToggleKey = Config.Bind(
                "Controls",
                "Toggle Spectator Key",
                new KeyboardShortcut(KeyCode.V),
                "Key to toggle spectating teammates while in the Downed state."
            );

            ShowOverlayInfo = Config.Bind(
                "UI",
                "Show On-Screen Helper",
                true,
                "Show the tactical Tarkov-styled banner when downed and when spectating."
            );

            UIPosition = Config.Bind(
                "UI",
                "Banner Position",
                "Top",
                "Position of the spectator banner. Options: 'Top' (recommended, completely clear of bottom timer), 'Center', 'UpperCenter'."
            );

            VerticalOffset = Config.Bind(
                "UI",
                "Vertical Offset",
                -75f,
                "Fine-tune vertical offset in pixels from the anchor point (negative moves downward)."
            );

            _setFirstPersonMethod = typeof(FreeCameraController).GetMethod(
                "SetPlayerToFirstPersonMode",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public
            );

            _deathFadeField = typeof(FreeCameraController).GetField(
                "_deathFade",
                BindingFlags.Instance | BindingFlags.NonPublic
            );

            PropertyInfo prop = typeof(FreeCameraController).GetProperty(
                "Player",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public
            );
            if (prop != null)
            {
                _getPlayerMethod = prop.GetGetMethod(true);
            }

            Logger.LogInfo("Fika Downed Spectator 1.0.0 loaded!");
        }

        private void Update()
        {
            if (!Singleton<GameWorld>.Instantiated || !Singleton<FreeCameraController>.Instantiated)
            {
                if (_isSpectatingWhileDowned)
                {
                    _isSpectatingWhileDowned = false;
                }
                _cachedPlate = null;
                HideUI();
                return;
            }

            var fcc = Singleton<FreeCameraController>.Instance;
            if (fcc == null)
            {
                HideUI();
                return;
            }

            var player = GetLocalPlayer(fcc);
            if (player == null)
            {
                HideUI();
                return;
            }

            bool isDowned = player.Downed;

            // If player was spectating while downed, but got REVIVED or DIED
            if (_isSpectatingWhileDowned && !isDowned)
            {
                Logger.LogInfo("Player is no longer downed (revived or dead)! Restoring camera.");
                ExitSpectating(fcc, player);
                HideUI();
                return;
            }

            // If not downed at all, ensure UI is hidden
            if (!isDowned)
            {
                HideUI();
                return;
            }

            // Check if toggle key pressed
            if (ToggleKey.Value.IsDown())
            {
                if (_isSpectatingWhileDowned)
                {
                    ExitSpectating(fcc, player);
                }
                else
                {
                    EnterSpectating(fcc, player);
                }
            }

            // Update on-screen tactical banner every frame while downed
            UpdateUI();
        }

        private FikaPlayer GetLocalPlayer(FreeCameraController fcc)
        {
            try
            {
                if (_getPlayerMethod != null)
                {
                    var p = _getPlayerMethod.Invoke(fcc, null) as FikaPlayer;
                    if (p != null) return p;
                }
            }
            catch { }

            if (Singleton<GameWorld>.Instantiated && Singleton<GameWorld>.Instance.MainPlayer != null)
            {
                return Singleton<GameWorld>.Instance.MainPlayer as FikaPlayer;
            }
            return null;
        }

        private PlayerPlateUI GetPlayerPlate()
        {
            if (_cachedPlate == null)
            {
                _cachedPlate = UnityEngine.Object.FindObjectOfType<PlayerPlateUI>();
            }
            return _cachedPlate;
        }

        private void EnterSpectating(FreeCameraController fcc, FikaPlayer player)
        {
            try
            {
                _isSpectatingWhileDowned = true;

                // 1. Toggle Fika's Spectate Camera
                fcc.ToggleSpectateCamera();

                // 2. Disable DeathFade effect on the camera
                if (_deathFadeField != null)
                {
                    var df = _deathFadeField.GetValue(fcc) as DeathFade;
                    if (df != null)
                    {
                        df.DisableEffect();
                        df.enabled = false;
                    }
                }

                // 3. Hide PlayerPlateUI black background image
                var plate = GetPlayerPlate();
                if (plate != null && plate.downedStateBackgroundScreen != null)
                {
                    plate.downedStateBackgroundScreen.gameObject.SetActive(false);
                }

                // 4. Ensure Preloader black image is completely transparent
                if (PreloaderUI.Instantiated && PreloaderUI.Instance != null)
                {
                    PreloaderUI.Instance.SetBlackImageAlpha(0f);
                }

                // 5. Restore FOV if clamped
                if (CameraManager.Exist && CameraManager.Instance.Camera != null)
                {
                    if (CameraManager.Instance.Camera.fieldOfView < 40f)
                    {
                        CameraManager.Instance.Camera.fieldOfView = 75f;
                    }
                }

                Logger.LogInfo("Entered Downed Spectator Mode cleanly!");
            }
            catch (Exception ex)
            {
                Logger.LogError("Error entering spectate mode: " + ex);
                _isSpectatingWhileDowned = false;
            }
        }

        private void ExitSpectating(FreeCameraController fcc, FikaPlayer player)
        {
            try
            {
                _isSpectatingWhileDowned = false;

                // 1. Return to first person
                if (_setFirstPersonMethod != null)
                {
                    _setFirstPersonMethod.Invoke(fcc, new object[] { player });
                }
                else
                {
                    fcc.ToggleCamera();
                }

                // 2. If still downed, re-enable natural black screen effects
                if (player != null && player.Downed)
                {
                    var plate = GetPlayerPlate();
                    if (plate != null && plate.downedStateBackgroundScreen != null)
                    {
                        plate.downedStateBackgroundScreen.gameObject.SetActive(true);
                    }

                    if (_deathFadeField != null)
                    {
                        var df = _deathFadeField.GetValue(fcc) as DeathFade;
                        if (df != null)
                        {
                            df.enabled = true;
                            df.EnableEffect();
                        }
                    }

                    // Keep PreloaderUI black curtain at 0 alpha so it NEVER blocks our UI!
                    if (PreloaderUI.Instantiated && PreloaderUI.Instance != null)
                    {
                        PreloaderUI.Instance.SetBlackImageAlpha(0f);
                    }
                }

                // 3. Immediately refresh UI so prompt is placed on top of black screen
                UpdateUI();

                Logger.LogInfo("Exited Downed Spectator Mode.");
            }
            catch (Exception ex)
            {
                Logger.LogError("Error exiting spectate mode: " + ex);
            }
        }

        #region Native Tarkov TextMeshPro UI

        private TMP_FontAsset GetTarkovBenderFont()
        {
            if (_cachedBenderFont != null) return _cachedBenderFont;

            try
            {
                // 1. Check default font asset in TMP_Settings
                if (TMP_Settings.defaultFontAsset != null)
                {
                    _cachedBenderFont = TMP_Settings.defaultFontAsset;
                    return _cachedBenderFont;
                }

                // 2. Check any TextMeshProUGUI under PreloaderUI
                if (PreloaderUI.Instantiated && PreloaderUI.Instance != null)
                {
                    var anyTmp = PreloaderUI.Instance.GetComponentInChildren<TextMeshProUGUI>();
                    if (anyTmp != null && anyTmp.font != null)
                    {
                        _cachedBenderFont = anyTmp.font;
                        return _cachedBenderFont;
                    }
                }

                // 3. Scan loaded TMP_FontAsset objects for Bender
                TMP_FontAsset[] fonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
                if (fonts != null)
                {
                    foreach (var f in fonts)
                    {
                        if (f != null && !string.IsNullOrEmpty(f.name))
                        {
                            string n = f.name.ToLower();
                            if (n.Contains("bender"))
                            {
                                _cachedBenderFont = f;
                                return _cachedBenderFont;
                            }
                        }
                    }
                }
            }
            catch { }

            return null;
        }

        private void EnsureUIInitialized()
        {
            if (_uiRoot != null) return;
            if (!PreloaderUI.Instantiated || PreloaderUI.Instance == null) return;

            try
            {
                // Create root GameObject
                _uiRoot = new GameObject("FikaDownedSpectatorUI");
                _uiRoot.layer = LayerMask.NameToLayer("UI");

                // Attach to Tarkov's main Preloader canvas children container
                PreloaderUI.Instance.SetCanvasAsParent(_uiRoot);
                _uiRoot.transform.SetAsLastSibling();

                // Setup RectTransform
                _uiRect = _uiRoot.GetComponent<RectTransform>();
                if (_uiRect == null)
                {
                    _uiRect = _uiRoot.AddComponent<RectTransform>();
                }
                ApplyUIPosition();

                // Setup tactical dark background image
                _bgImage = _uiRoot.GetComponent<Image>();
                if (_bgImage == null)
                {
                    _bgImage = _uiRoot.AddComponent<Image>();
                }
                _bgImage.color = new Color(0.05f, 0.06f, 0.07f, 0.88f);
                _bgImage.raycastTarget = false;

                // Create text container
                GameObject textObj = new GameObject("Label");
                textObj.transform.SetParent(_uiRoot.transform, false);

                _textRect = textObj.GetComponent<RectTransform>();
                if (_textRect == null)
                {
                    _textRect = textObj.AddComponent<RectTransform>();
                }
                _textRect.anchorMin = Vector2.zero;
                _textRect.anchorMax = Vector2.one;
                _textRect.offsetMin = Vector2.zero;
                _textRect.offsetMax = Vector2.zero;
                _textRect.pivot = new Vector2(0.5f, 0.5f);

                // Setup TextMeshProUGUI with Tarkov Bender font
                _tmpText = textObj.AddComponent<TextMeshProUGUI>();
                _tmpText.horizontalAlignment = HorizontalAlignmentOptions.Center;
                _tmpText.verticalAlignment = VerticalAlignmentOptions.Middle;
                _tmpText.fontSize = 17f;
                _tmpText.characterSpacing = 2f;
                _tmpText.richText = true;
                _tmpText.raycastTarget = false;

                var font = GetTarkovBenderFont();
                if (font != null)
                {
                    _tmpText.font = font;
                    if (font.material != null)
                    {
                        _tmpText.fontSharedMaterial = font.material;
                    }
                }

                _uiRoot.SetActive(false);
                Logger.LogInfo("FikaDownedSpectator native UI initialized successfully!");
            }
            catch (Exception ex)
            {
                Logger.LogError("Failed to initialize native Tarkov UI: " + ex);
                _uiRoot = null;
            }
        }

        private void ApplyUIPosition()
        {
            if (_uiRect == null) return;

            string pos = UIPosition.Value != null ? UIPosition.Value.Trim().ToLower() : "top";
            float offset = VerticalOffset.Value;

            if (pos == "center")
            {
                _uiRect.anchorMin = new Vector2(0.5f, 0.5f);
                _uiRect.anchorMax = new Vector2(0.5f, 0.5f);
                _uiRect.pivot = new Vector2(0.5f, 0.5f);
                _uiRect.anchoredPosition = new Vector2(0f, offset);
            }
            else if (pos == "uppercenter")
            {
                _uiRect.anchorMin = new Vector2(0.5f, 0.5f);
                _uiRect.anchorMax = new Vector2(0.5f, 0.5f);
                _uiRect.pivot = new Vector2(0.5f, 0.5f);
                _uiRect.anchoredPosition = new Vector2(0f, offset <= -70f ? 160f : offset);
            }
            else // "Top" (default - completely clear of bottom timer!)
            {
                _uiRect.anchorMin = new Vector2(0.5f, 1f);
                _uiRect.anchorMax = new Vector2(0.5f, 1f);
                _uiRect.pivot = new Vector2(0.5f, 1f);
                _uiRect.anchoredPosition = new Vector2(0f, offset > 0f ? -75f : offset);
            }
        }

        private void UpdateUI()
        {
            if (!ShowOverlayInfo.Value)
            {
                HideUI();
                return;
            }

            EnsureUIInitialized();

            if (_uiRoot == null || _tmpText == null || _uiRect == null) return;

            // Ensure UI stays on top of any newly activated elements
            _uiRoot.transform.SetAsLastSibling();

            ApplyUIPosition();

            string text;
            if (_isSpectatingWhileDowned)
            {
                // Spectating Active Banner
                text = string.Format(
                    "<color=#38E07B><b>● LIVE SPECTATING</b></color>   <color=#4B5563>|</color>   <color=#E1DFD6>Watching Teammate</color>   <color=#4B5563>|</color>   <color=#E5B358><b>[ {0} ]</b></color> <color=#E1DFD6>Return to Body</color>   <color=#4B5563>|</color>   <color=#9CA3AF>LMB / RMB: Switch</color>",
                    ToggleKey.Value.MainKey
                );
            }
            else
            {
                // Downed Prompt Banner (Always visible on top of black screen, zero overlap with bottom seconds!)
                text = string.Format(
                    "<color=#E5B358><b>[ {0} ]</b></color>  <color=#E1DFD6>SPECTATE TEAMMATES</color>",
                    ToggleKey.Value.MainKey
                );
            }

            _tmpText.text = text;
            _tmpText.horizontalAlignment = HorizontalAlignmentOptions.Center;
            _tmpText.verticalAlignment = VerticalAlignmentOptions.Middle;
            _tmpText.ForceMeshUpdate();

            // Auto-fit box width to text with exactly 28px symmetrical padding on each side
            float boxWidth = Mathf.Max(_tmpText.preferredWidth + 56f, 360f);
            _uiRect.sizeDelta = new Vector2(boxWidth, 38f);

            // Ensure child textRect matches _uiRect size perfectly with zero offset
            if (_textRect != null)
            {
                _textRect.anchorMin = Vector2.zero;
                _textRect.anchorMax = Vector2.one;
                _textRect.offsetMin = Vector2.zero;
                _textRect.offsetMax = Vector2.zero;
            }

            if (!_uiRoot.activeSelf)
            {
                _uiRoot.SetActive(true);
            }
        }

        private void HideUI()
        {
            if (_uiRoot != null && _uiRoot.activeSelf)
            {
                _uiRoot.SetActive(false);
            }
        }

        private void OnDestroy()
        {
            if (_uiRoot != null)
            {
                Destroy(_uiRoot);
                _uiRoot = null;
            }
        }

        #endregion

        #region IMGUI Fallback

        private void OnGUI()
        {
            if (!ShowOverlayInfo.Value) return;

            // If native UGUI is active and rendering, we do not need IMGUI
            if (_uiRoot != null && _uiRoot.activeSelf) return;

            // Failsafe: If UGUI was not active, display via IMGUI so the player is never left without text
            if (!Singleton<GameWorld>.Instantiated || !Singleton<FreeCameraController>.Instantiated) return;
            var fcc = Singleton<FreeCameraController>.Instance;
            if (fcc == null) return;
            var player = GetLocalPlayer(fcc);
            if (player == null || !player.Downed) return;

            string text = _isSpectatingWhileDowned
                ? string.Format("[ LIVE SPECTATING ]  Watching Teammate  |  [{0}] Return to Body  |  LMB/RMB: Switch", ToggleKey.Value.MainKey)
                : string.Format("[ {0} ]  SPECTATE TEAMMATES", ToggleKey.Value.MainKey);

            float width = _isSpectatingWhileDowned ? 720f : 440f;
            float height = 34f;
            float x = (Screen.width - width) / 2f;
            float y = 50f;

            GUI.Box(new Rect(x, y, width, height), text);
        }

        #endregion
    }
}
