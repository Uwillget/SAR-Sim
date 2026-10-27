using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;

namespace RoadNetworkEditor.RuntimeUI
{
    [Serializable]
    public sealed class SarRuntimeLocalizationCatalog
    {
        [JsonProperty("locale")]
        public string Locale = SarRuntimeLocalization.DefaultLocale;

        [JsonProperty("exact")]
        public Dictionary<string, string> Exact = new Dictionary<string, string>(StringComparer.Ordinal);

        [JsonProperty("phrases")]
        public Dictionary<string, string> Phrases = new Dictionary<string, string>(StringComparer.Ordinal);

        public static SarRuntimeLocalizationCatalog FromJson(string json)
        {
            SarRuntimeLocalizationCatalog catalog = JsonConvert.DeserializeObject<SarRuntimeLocalizationCatalog>(
                json ?? string.Empty) ?? new SarRuntimeLocalizationCatalog();
            catalog.Exact = catalog.Exact != null
                ? new Dictionary<string, string>(catalog.Exact, StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);
            catalog.Phrases = catalog.Phrases != null
                ? new Dictionary<string, string>(catalog.Phrases, StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);
            return catalog;
        }
    }

    /// <summary>
    /// Provides deterministic source-string localization for the generated Windows-player UI.
    /// The workbench translates text when it creates or updates a UI control; this component does
    /// not scan Canvas objects, poll text values, or mutate labels during rendering.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SarRuntimeLocalization : MonoBehaviour
    {
        public const string DefaultLocale = "en-US";
        public const string ChineseLocale = "zh-CN";
        public const string LocaleFileName = "SAR.locale.json";
        public const string CatalogFolderName = "SARLocalization";
        private const char LiteralStart = '\uE100';
        private const char LiteralEnd = '\uE101';

        private sealed class LocaleSelection
        {
            [JsonProperty("locale")]
            public string Locale;
        }

        private List<KeyValuePair<string, string>> orderedPhrases =
            new List<KeyValuePair<string, string>>();
        private List<KeyValuePair<string, string>> orderedExactFragments =
            new List<KeyValuePair<string, string>>();
        private Dictionary<string, string> exact = new Dictionary<string, string>(StringComparer.Ordinal);

        public string Locale { get; private set; } = DefaultLocale;
        public bool IsTranslationActive => !string.Equals(Locale, DefaultLocale, StringComparison.OrdinalIgnoreCase);

        public static SarRuntimeLocalization Ensure(GameObject host)
        {
            if (host == null) return null;
            SarRuntimeLocalization localization = host.GetComponent<SarRuntimeLocalization>();
            if (localization == null) localization = host.AddComponent<SarRuntimeLocalization>();
            return localization;
        }

        private void Awake()
        {
            if (Application.platform != RuntimePlatform.WindowsPlayer)
            {
                Configure(DefaultLocale, null);
                return;
            }

            string requestedLocale = ReadConfiguredLocale(ReleaseLocaleFilePath());
            if (string.Equals(requestedLocale, DefaultLocale, StringComparison.OrdinalIgnoreCase))
            {
                Configure(DefaultLocale, null);
                return;
            }

            string catalogPath = Path.Combine(Application.streamingAssetsPath, CatalogFolderName,
                requestedLocale + ".json");
            try
            {
                if (!File.Exists(catalogPath))
                {
                    Debug.LogWarning($"[SAR Localization] Catalog not found for '{requestedLocale}': {catalogPath}. Falling back to {DefaultLocale}.");
                    Configure(DefaultLocale, null);
                    return;
                }
                Configure(requestedLocale, SarRuntimeLocalizationCatalog.FromJson(File.ReadAllText(catalogPath)));
                Debug.Log($"[SAR Localization] Runtime UI locale: {Locale}.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SAR Localization] Could not load '{requestedLocale}': {ex.Message}. Falling back to {DefaultLocale}.");
                Configure(DefaultLocale, null);
            }
        }

        public void ConfigureForTests(string locale, SarRuntimeLocalizationCatalog catalog)
        {
            Configure(locale, catalog);
        }

        public void Configure(string locale, SarRuntimeLocalizationCatalog catalog)
        {
            Locale = NormalizeLocale(locale);
            exact = catalog?.Exact != null
                ? new Dictionary<string, string>(catalog.Exact, StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);
            orderedPhrases = (catalog?.Phrases ?? new Dictionary<string, string>())
                .Where(pair => !string.IsNullOrEmpty(pair.Key) && pair.Value != null)
                .OrderByDescending(pair => pair.Key.Length)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .ToList();
            // Exact entries are also valid fixed fragments only in a sentence that
            // explicitly marks authored data with Literal(...).  Restricting this pass
            // to literal-bearing strings prevents an exact label such as "Metrics" from
            // rewriting a filename such as "MetricsTable.csv" in ordinary notices.
            orderedExactFragments = (catalog?.Exact ?? new Dictionary<string, string>())
                .Where(pair => !string.IsNullOrEmpty(pair.Key) && pair.Value != null)
                .OrderByDescending(pair => pair.Key.Length)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .ToList();
            if (string.Equals(Locale, ChineseLocale, StringComparison.OrdinalIgnoreCase) && catalog == null)
                Locale = DefaultLocale;
        }

        public string Translate(string source)
        {
            if (string.IsNullOrEmpty(source)) return source;
            var literals = new List<string>();
            string protectedSource = ExtractLiterals(source, literals);
            string result = protectedSource;
            if (IsTranslationActive)
            {
                if (exact.TryGetValue(protectedSource, out string translated)) result = translated;
                else
                {
                    foreach (KeyValuePair<string, string> phrase in orderedPhrases)
                        if (result.IndexOf(phrase.Key, StringComparison.Ordinal) >= 0)
                            result = result.Replace(phrase.Key, phrase.Value);
                    if (literals.Count > 0)
                        foreach (KeyValuePair<string, string> fragment in orderedExactFragments)
                            if (result.IndexOf(fragment.Key, StringComparison.Ordinal) >= 0)
                                result = result.Replace(fragment.Key, fragment.Value);
                }
            }
            for (int index = 0; index < literals.Count; index++)
                result = result.Replace(LiteralPlaceholder(index), literals[index]);
            return result;
        }

        /// <summary>
        /// Marks user-authored text (scene names, layer names, IDs) as data rather
        /// than UI copy. Phrase localization can then translate the surrounding
        /// sentence without rewriting a name such as "Simulation" or "Default Layer".
        /// </summary>
        public static string Literal(string value)
        {
            string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? string.Empty));
            return LiteralStart + encoded + LiteralEnd;
        }

        private static string ExtractLiterals(string source, List<string> literals)
        {
            var result = new StringBuilder(source.Length);
            int cursor = 0;
            while (cursor < source.Length)
            {
                int start = source.IndexOf(LiteralStart, cursor);
                if (start < 0)
                {
                    result.Append(source, cursor, source.Length - cursor);
                    break;
                }
                result.Append(source, cursor, start - cursor);
                int end = source.IndexOf(LiteralEnd, start + 1);
                if (end < 0)
                {
                    result.Append(source, start, source.Length - start);
                    break;
                }
                string encoded = source.Substring(start + 1, end - start - 1);
                try
                {
                    literals.Add(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
                    result.Append(LiteralPlaceholder(literals.Count - 1));
                }
                catch (FormatException)
                {
                    result.Append(source, start, end - start + 1);
                }
                cursor = end + 1;
            }
            return result.ToString();
        }

        private static string LiteralPlaceholder(int index) => $"\uE110{index}\uE111";

        public static string NormalizeLocale(string locale)
        {
            string value = (locale ?? string.Empty).Trim().Replace('_', '-');
            if (value.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) return ChineseLocale;
            return DefaultLocale;
        }

        public static string ReadConfiguredLocale(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return DefaultLocale;
            try
            {
                string json = File.ReadAllText(path).Trim();
                if (json.StartsWith("\"", StringComparison.Ordinal))
                    return NormalizeLocale(JsonConvert.DeserializeObject<string>(json));
                LocaleSelection selection = JsonConvert.DeserializeObject<LocaleSelection>(json);
                return NormalizeLocale(selection?.Locale);
            }
            catch
            {
                return DefaultLocale;
            }
        }

        public static string ReleaseLocaleFilePath()
        {
            string releaseDirectory = Path.GetDirectoryName(Application.dataPath);
            if (string.IsNullOrWhiteSpace(releaseDirectory)) releaseDirectory = AppContext.BaseDirectory;
            return Path.Combine(releaseDirectory, LocaleFileName);
        }

    }
}
