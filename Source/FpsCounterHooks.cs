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
    // height, 12 px margin; "<n> FPS" averaged over 0.25 s of unscaled time. Off hides the object (SetActive false), so
    // its Update does not run at all; the object is kept for the next toggle. No patches.
    // 3.1.0: anchored top left by default; color, font, size and position (X/Y percent of the screen) are Settings rows
    // that apply live. Black text gets a light outline. The text is kept on screen.
    internal static class FpsCounterHooks
    {
        internal enum CounterColor { White, Yellow, Green, Cyan, Orange, Red, Black }
        internal enum CounterFont { Game, Default }

        internal static readonly Feature Counter = new Feature("Graphics/FpsCounter");
        internal static ConfigEntry<bool> Entry;
        internal static ConfigEntry<CounterColor> ColorEntry;
        internal static ConfigEntry<CounterFont> FontEntry;
        internal static ConfigEntry<int> SizeEntry, XEntry, YEntry;
        private static GameObject root;
        private static FpsCounterView view;
        private static bool broken;

        internal static void Start()
        {
            if (Entry == null) return;
            Entry.SettingChanged += Changed;
            if (ColorEntry != null) ColorEntry.SettingChanged += LookChanged;
            if (FontEntry != null) FontEntry.SettingChanged += LookChanged;
            if (SizeEntry != null) SizeEntry.SettingChanged += LookChanged;
            if (XEntry != null) XEntry.SettingChanged += LookChanged;
            if (YEntry != null) YEntry.SettingChanged += LookChanged;
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
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.sizeDelta = new Vector2(600f, 100f);

            Text label = textObject.AddComponent<Text>();
            label.alignment = TextAnchor.UpperLeft;
            label.horizontalOverflow = HorizontalWrapMode.Overflow;
            label.verticalOverflow = VerticalWrapMode.Overflow;
            label.raycastTarget = false;
            label.supportRichText = false;
            label.fontStyle = FontStyle.Bold;
            label.color = Color.white;
            label.text = "";

            // Outline first, then a soft offset shadow under text + outline: readable on any background.
            Outline outline = textObject.AddComponent<Outline>();
            outline.effectColor = FpsCounterView.DarkEdge;
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

        // Color, font, size, X or Y changed on the settings page; the counter object (if built) takes it at once.
        private static void LookChanged(object sender, EventArgs e)
        {
            try
            {
                if (view != null && !broken) view.ApplyLook();
            }
            catch (Exception ex) { Broke(ex); }
        }

        internal static Color TextColor()
        {
            switch (ColorEntry == null ? CounterColor.White : ColorEntry.Value)
            {
                case CounterColor.Yellow: return new Color(1f, 0.9f, 0.15f, 1f);
                case CounterColor.Green: return new Color(0.35f, 1f, 0.35f, 1f);
                case CounterColor.Cyan: return new Color(0.3f, 0.95f, 1f, 1f);
                case CounterColor.Orange: return new Color(1f, 0.6f, 0.15f, 1f);
                case CounterColor.Red: return new Color(1f, 0.25f, 0.25f, 1f);
                case CounterColor.Black: return Color.black;
                default: return Color.white;
            }
        }

        internal static bool BlackText()
        {
            return ColorEntry != null && ColorEntry.Value == CounterColor.Black;
        }

        internal static bool GameFontWanted()
        {
            return FontEntry == null || FontEntry.Value == CounterFont.Game;
        }

        // Entry value clamped to its range; the default when the entry is missing.
        internal static int Number(ConfigEntry<int> entry, int fallback, int min, int max)
        {
            int value = entry == null ? fallback : entry.Value;
            return value < min ? min : (value > max ? max : value);
        }

        internal static void Broke(Exception e)
        {
            broken = true;
            Counter.Fail(e);
        }

        internal static string Status()
        {
            bool shown = root != null && root.activeSelf;
            return "active=" + Counter.Active + " shown=" + shown + " text=" + (view != null && view.Label != null ? view.Label.text : "(none)") + " gameFont=" + (view != null && view.GameFontSet)
                + " color=" + (ColorEntry == null ? "-" : ColorEntry.Value.ToString()) + " font=" + (FontEntry == null ? "-" : FontEntry.Value.ToString())
                + " size=" + Number(SizeEntry, 28, 10, 80) + " x=" + Number(XEntry, 0, 0, 100) + " y=" + Number(YEntry, 0, 0, 100)
                + " pos=" + (view != null && view.Label != null ? view.Label.rectTransform.anchoredPosition.ToString() : "-");
        }
    }

    internal sealed class FpsCounterView : MonoBehaviour
    {
        private const float Interval = 0.25f, Margin = 12f;
        // Width reference so the on-screen clamp does not move the text when the number of digits changes.
        private const string WidestText = "0000 FPS";
        internal static readonly Color DarkEdge = new Color(0.1f, 0.06f, 0.03f, 0.9f), LightEdge = new Color(0.95f, 0.95f, 0.95f, 0.9f);
        internal Text Label;
        internal Outline Edge;
        internal Shadow Drop;
        internal bool GameFontSet;
        private Font gameFont;
        private int lastScreenHeight, lastScreenWidth, frames;
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
                if ((!GameFontSet && FpsCounterHooks.GameFontWanted()) || lastScreenHeight != Screen.height || lastScreenWidth != Screen.width) ApplyLook();
                Label.text = Mathf.RoundToInt(frames / elapsed).ToString(CultureInfo.InvariantCulture) + " FPS";
                frames = 0;
                windowStart = now;
            }
            catch (Exception e) { FpsCounterHooks.Broke(e); }
        }

        internal void ApplyLook()
        {
            if (FpsCounterHooks.GameFontWanted())
            {
                if (!GameFontSet)
                {
                    gameFont = GameFont();
                    GameFontSet = gameFont != null;
                }
                Label.font = gameFont ?? Resources.GetBuiltinResource<Font>("Arial.ttf");
            }
            else Label.font = Resources.GetBuiltinResource<Font>("Arial.ttf");

            Label.color = FpsCounterHooks.TextColor();
            Edge.effectColor = FpsCounterHooks.BlackText() ? LightEdge : DarkEdge;

            int width = Screen.width, height = Screen.height;
            lastScreenHeight = height;
            lastScreenWidth = width;
            float scale = height / 1080f;
            Label.fontSize = Mathf.Max(8, Mathf.RoundToInt(FpsCounterHooks.Number(FpsCounterHooks.SizeEntry, 28, 10, 80) * scale));
            float m = Margin * scale;
            float x = m + FpsCounterHooks.Number(FpsCounterHooks.XEntry, 0, 0, 100) / 100f * width;
            float y = m + FpsCounterHooks.Number(FpsCounterHooks.YEntry, 0, 0, 100) / 100f * height;
            // Keep the whole text on screen: right/bottom limit from the widest counter text at the current font and size.
            TextGenerationSettings settings = Label.GetGenerationSettings(Vector2.zero);
            float ppu = Label.pixelsPerUnit > 0f ? Label.pixelsPerUnit : 1f;
            float textWidth = Label.cachedTextGeneratorForLayout.GetPreferredWidth(WidestText, settings) / ppu;
            float textHeight = Label.cachedTextGeneratorForLayout.GetPreferredHeight(WidestText, settings) / ppu;
            x = Mathf.Max(m, Mathf.Min(x, width - textWidth - m));
            y = Mathf.Max(m, Mathf.Min(y, height - textHeight - m));
            Label.rectTransform.anchoredPosition = new Vector2(x, -y);
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
