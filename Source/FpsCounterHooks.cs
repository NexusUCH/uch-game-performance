using System;
using System.Globalization;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.UI;

namespace UCHGamePerformance
{
    // 2.15.0: Settings / "Show FPS counter" (Feature Graphics/FpsCounter), off by default, live toggle. Port of the
    // stand-alone UCH FPS Counter 1.0.0 (same author): overlay canvas (sorting order 32760), bold text with outline and
    // shadow, the game's own UI font (chat text prefab) with Arial as fallback, 28 px at 1080p scaled by the screen
    // height, 12 px margin, anchored top right; "<n> FPS" averaged over 0.25 s of unscaled time. Off hides the object
    // (SetActive false), so its Update does not run at all; the object is kept for the next toggle. No patches.
    internal static class FpsCounterHooks
    {
        internal static readonly Feature Counter = new Feature("Graphics/FpsCounter");
        internal static ConfigEntry<bool> Entry;
        private static GameObject root;
        private static FpsCounterView view;
        private static bool broken;

        internal static void Start()
        {
            if (Entry == null) return;
            Entry.SettingChanged += Changed;
            Counter.Start(Entry.Value, Install, null);
        }

        private static void Install()
        {
            Counter.Restore = Hide;
            if (root == null) Build();
            Show();
        }

        private static void Build()
        {
            root = new GameObject("UCH Game Performance FPS Counter");
            root.SetActive(false);
            UnityEngine.Object.DontDestroyOnLoad(root);
            Canvas canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32760;

            GameObject textObject = new GameObject("Text", typeof(RectTransform));
            textObject.transform.SetParent(root.transform, false);
            RectTransform rt = (RectTransform)textObject.transform;
            rt.anchorMin = new Vector2(1f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(1f, 1f);
            rt.sizeDelta = new Vector2(600f, 100f);

            Text label = textObject.AddComponent<Text>();
            label.alignment = TextAnchor.UpperRight;
            label.horizontalOverflow = HorizontalWrapMode.Overflow;
            label.verticalOverflow = VerticalWrapMode.Overflow;
            label.raycastTarget = false;
            label.supportRichText = false;
            label.fontStyle = FontStyle.Bold;
            label.color = Color.white;
            label.text = "";

            // Dark outline first, then a soft offset shadow under text + outline: readable on any background.
            Outline outline = textObject.AddComponent<Outline>();
            outline.effectColor = new Color(0.1f, 0.06f, 0.03f, 0.9f);
            Shadow shadow = textObject.AddComponent<Shadow>();
            shadow.effectColor = new Color(0f, 0f, 0f, 0.45f);

            view = root.AddComponent<FpsCounterView>();
            view.Label = label;
            view.Edge = outline;
            view.Drop = shadow;
            view.ApplyLook();
        }

        private static void Show()
        {
            view.ResetWindow();
            root.SetActive(true);
        }

        private static void Hide()
        {
            if (root != null) root.SetActive(false);
        }

        private static void Changed(object sender, EventArgs e)
        {
            try
            {
                if (Entry.Value)
                {
                    if (Counter.Active || broken) return;
                    if (root == null || view == null) { Counter.Start(true, Install, null); return; }
                    Counter.Active = true;
                    Show();
                    Feature.Log.LogInfo("Graphics/FpsCounter on (live)");
                }
                else
                {
                    if (!Counter.Active) return;
                    Counter.Active = false;
                    Hide();
                    Feature.Log.LogInfo("Graphics/FpsCounter off (live)");
                }
            }
            catch (Exception ex) { Broke(ex); }
        }

        internal static void Broke(Exception e)
        {
            broken = true;
            Counter.Fail(e);
        }

        internal static string Status()
        {
            bool shown = root != null && root.activeSelf;
            return "active=" + Counter.Active + " shown=" + shown + " text=" + (view != null && view.Label != null ? view.Label.text : "(none)") + " gameFont=" + (view != null && view.GameFontSet);
        }
    }

    internal sealed class FpsCounterView : MonoBehaviour
    {
        private const float Interval = 0.25f, FontSize = 28f, Margin = 12f;
        internal Text Label;
        internal Outline Edge;
        internal Shadow Drop;
        internal bool GameFontSet;
        private int lastScreenHeight, frames;
        private float windowStart;

        internal void ResetWindow()
        {
            frames = 0;
            windowStart = Time.unscaledTime;
        }

        private void Update()
        {
            try
            {
                frames++;
                float now = Time.unscaledTime, elapsed = now - windowStart;
                if (elapsed < Interval) return;
                if (!GameFontSet || lastScreenHeight != Screen.height) ApplyLook();
                Label.text = Mathf.RoundToInt(frames / elapsed).ToString(CultureInfo.InvariantCulture) + " FPS";
                frames = 0;
                windowStart = now;
            }
            catch (Exception e) { FpsCounterHooks.Broke(e); }
        }

        internal void ApplyLook()
        {
            if (!GameFontSet)
            {
                Font font = GameFont();
                GameFontSet = font != null;
                Label.font = font ?? Resources.GetBuiltinResource<Font>("Arial.ttf");
            }
            lastScreenHeight = Screen.height;
            float scale = Screen.height / 1080f;
            Label.fontSize = Mathf.Max(8, Mathf.RoundToInt(FontSize * scale));
            float m = Margin * scale;
            Label.rectTransform.anchoredPosition = new Vector2(-m, -m);
            float edge = Mathf.Max(1f, 2f * scale);
            Edge.effectDistance = new Vector2(edge, -edge);
            Drop.effectDistance = new Vector2(edge * 1.5f, -edge * 2f);
        }

        // The game's own UI font, taken from the chat text prefab once the chat system exists.
        private static Font GameFont()
        {
            try
            {
                if (GameState.ChatSystem != null && GameState.ChatSystem.ChatUnitPrefab != null && GameState.ChatSystem.ChatUnitPrefab.chatText != null)
                    return GameState.ChatSystem.ChatUnitPrefab.chatText.font;
            }
            catch (Exception)
            {
            }
            return null;
        }
    }
}
