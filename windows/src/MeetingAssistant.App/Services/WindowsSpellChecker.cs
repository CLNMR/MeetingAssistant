using System.IO;
using System.Runtime.InteropServices;
using MeetingAssistant.Core;
using MeetingAssistant.Core.Text;

namespace MeetingAssistant.App.Services;

/// "Known word" check for the vocabulary (spec 04 §5.3) with the Windows Spell Checking API
/// (`ISpellCheckerFactory`, Windows 8+): a word is known when the German (de-DE) or the English
/// (en-US) checker reports no error for it. When neither language is installed, word lists in
/// `<app data>\Dictionaries` (plain lists or Hunspell `.dic`) are used instead.
/// Windows notes: replaces NSSpellChecker on the Mac.
public static class WindowsSpellChecker
{
    static readonly string[] Languages = ["de-DE", "en-US"];

    /// The checker to put into <see cref="SpellCheck.Checker"/>, or null when nothing is available.
    public static Func<string, bool>? Create()
    {
        try
        {
            var factory = (ISpellCheckerFactory)new SpellCheckerFactoryClass();
            var supported = Languages.Where(l => factory.IsSupported(l) != 0).ToArray();
            if (supported.Length > 0)
                return new ThreadCheckers(supported).IsKnown;
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Spell checker unavailable: {ex.Message}"); }

        var dir = Path.Combine(AppPaths.Root, "Dictionaries");
        if (!Directory.Exists(dir)) return null;
        var lists = new WordListSpellChecker(Directory.GetFiles(dir, "*.dic").Concat(Directory.GetFiles(dir, "*.txt")));
        return lists.Count > 0 ? lists.IsKnown : null;
    }

    /// COM spell checkers are created per thread (the UI thread is STA, the queue's threads MTA).
    sealed class ThreadCheckers(string[] languages)
    {
        readonly ThreadLocal<ISpellChecker[]> _checkers = new(() =>
        {
            var factory = (ISpellCheckerFactory)new SpellCheckerFactoryClass();
            return languages.Select(factory.CreateSpellChecker).ToArray();
        });

        public bool IsKnown(string word)
        {
            foreach (var checker in _checkers.Value!)
            {
                var errors = checker.Check(word);
                try
                {
                    // S_FALSE (1): no (more) errors, so the word is spelled correctly.
                    if (errors.Next(out var error) != 0) return true;
                    if (error is not null) Marshal.ReleaseComObject(error);
                }
                finally { Marshal.ReleaseComObject(errors); }
            }
            return false;
        }
    }

    [ComImport, Guid("7AB36653-1796-484B-BDFA-E74F1DB7C1DC")]
    class SpellCheckerFactoryClass;

    [ComImport, Guid("8E018A9D-2415-4677-BF08-794EA61F94BB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISpellCheckerFactory
    {
        [return: MarshalAs(UnmanagedType.Interface)] object get_SupportedLanguages();
        int IsSupported([MarshalAs(UnmanagedType.LPWStr)] string languageTag);
        ISpellChecker CreateSpellChecker([MarshalAs(UnmanagedType.LPWStr)] string languageTag);
    }

    [ComImport, Guid("B6FD0B71-E2BC-4653-8D05-F197E412770B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISpellChecker
    {
        [return: MarshalAs(UnmanagedType.LPWStr)] string get_LanguageTag();
        IEnumSpellingError Check([MarshalAs(UnmanagedType.LPWStr)] string text);
    }

    [ComImport, Guid("803E3BD4-2828-4410-8290-418D1D73C762"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IEnumSpellingError
    {
        [PreserveSig] int Next([MarshalAs(UnmanagedType.Interface)] out object? value);
    }
}
