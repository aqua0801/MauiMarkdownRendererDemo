using System.Text.RegularExpressions;

namespace MauiMarkdownRendererWithLaTeX
{
    /// <summary>
    /// Represents a pair of colors for light and dark themes.
    /// </summary>
    /// <param name="LightColor">The color to use for the light theme variant.</param>
    /// <param name="DarkColor">The color to use for the dark theme variant.</param>
    public record struct ThemeColors(Color LightColor, Color DarkColor);

    public sealed class TokenRule
    {
        public string Scope { get; }
        public Regex Pattern { get; }

        public TokenRule(string scope, string pattern,
            RegexOptions options = RegexOptions.None)
        {
            Scope = scope;
            Pattern = new Regex(pattern, options | RegexOptions.Compiled);
        }
    }

    /// <summary>
    /// Represents the definition of a programming language, including its unique identifier, aliases, and tokenization
    /// rules.
    /// </summary>
    /// <remarks>A language definition is used to identify and describe a language for purposes such as syntax
    /// highlighting or parsing. The definition includes a primary identifier, a set of alternative names (aliases), and
    /// a collection of tokenization rules that describe how the language should be parsed or highlighted. Instances of
    /// this class are immutable after construction, except for the token rules, which can be modified via the Rules
    /// property.</remarks>
    public sealed class LanguageDefinition
    {
        public string Id { get; }
        public string[] Aliases { get; }
        public List<TokenRule> Rules { get; } = new();

        public LanguageDefinition(string id, params string[] aliases)
        {
            Id = id;
            Aliases = aliases;
        }

        public bool Matches(string name) =>
            Id.Equals(name, StringComparison.OrdinalIgnoreCase) ||
            Aliases.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Provides syntax highlighting for source code strings using language definitions and customizable color themes.
    /// </summary>
    /// <remarks>The CodeColorizer class supports multiple programming languages and allows registration of
    /// custom language definitions. It exposes properties for font customization and a color map for theme-based
    /// highlighting. The class is designed for use in applications that need to display formatted, colorized code, such
    /// as editors or viewers. Thread safety is not guaranteed; if used from multiple threads, external synchronization
    /// is required.</remarks>
    public class CodeColorizer
    {
        public static readonly string MonospaceFont =
#if WINDOWS
            "Cascadia Code, Consolas, Courier New"
#elif ANDROID
            "monospace"
#elif IOS || MACCATALYST
            "Menlo"
#else
            "Courier New"
#endif
            ;

        public double FontSize { get; set; } = 14.0;
        public string FontFamily { get; set; } = MonospaceFont;

        public Dictionary<string, ThemeColors> ColorMap { get; } = new()
        {
            ["Keyword"] = new(Color.FromArgb("#0000FF"), Color.FromArgb("#569CD6")),
            ["String"] = new(Color.FromArgb("#A31515"), Color.FromArgb("#CE9178")),
            ["Comment"] = new(Color.FromArgb("#008000"), Color.FromArgb("#6A9955")),
            ["Number"] = new(Color.FromArgb("#098658"), Color.FromArgb("#B5CEA8")),
            ["Type"] = new(Color.FromArgb("#2B91AF"), Color.FromArgb("#4EC9B0")),
            ["Method"] = new(Color.FromArgb("#795E26"), Color.FromArgb("#DCDCAA")),
            ["Property"] = new(Color.FromArgb("#6600EE"), Color.FromArgb("#9CDCFE")),
            ["Constant"] = new(Color.FromArgb("#B000B0"), Color.FromArgb("#4FC1FF")),
            ["Attribute"] = new(Color.FromArgb("#808000"), Color.FromArgb("#C8C896")),
            ["Preprocessor"] = new(Color.FromArgb("#A00000"), Color.FromArgb("#C586C0")),
            ["Operator"] = new(Color.FromArgb("#000000"), Color.FromArgb("#D4D4D4")),
            ["PlainText"] = new(Color.FromArgb("#000000"), Color.FromArgb("#D4D4D4")),
        };

        private readonly List<LanguageDefinition> _languages = new();

        public CodeColorizer() => RegisterLanguages();

        /// <summary>
        /// Returns a syntax-highlighted representation of the specified code using the given programming language.
        /// </summary>
        /// <param name="code">The source code to be highlighted.</param>
        /// <param name="language">The name of the programming language to use for syntax highlighting. If null or unrecognized, the code is
        /// returned as plain text.</param>
        /// <returns>A formatted string containing the highlighted code. If the language is not recognized, the code is returned
        /// without syntax highlighting.</returns>

        public FormattedString Highlight(string code, string? language)
        {
            var lang = ResolveLanguage(language);
            return lang == null ? PlainText(code) : Tokenize(code, lang);
        }

        public void Register(LanguageDefinition lang)
        {
            var existing = _languages.FirstOrDefault(l => l.Matches(lang.Id));
            if (existing != null) _languages.Remove(existing);
            _languages.Add(lang);
        }

        private Color Resolve(string scope)
        {
            var key = ColorMap.ContainsKey(scope) ? scope : "PlainText";
            var tc = ColorMap[key];
            return Application.Current?.RequestedTheme == AppTheme.Dark
                ? tc.DarkColor : tc.LightColor;
        }

        private FormattedString Tokenize(string code, LanguageDefinition lang)
        {
            var formatted = new FormattedString();
            int pos = 0, len = code.Length;

            while (pos < len)
            {
                TokenRule? winner = null;
                Match? bestMatch = null;

                foreach (var rule in lang.Rules)
                {
                    var m = rule.Pattern.Match(code, pos);
                    if (!m.Success) continue;
                    if (bestMatch == null || m.Index < bestMatch.Index)
                    {
                        bestMatch = m;
                        winner = rule;
                    }
                }

                if (bestMatch == null || winner == null)
                {
                    AddSpan(formatted, code.Substring(pos), "PlainText");
                    break;
                }

                if (bestMatch.Index > pos)
                    AddSpan(formatted, code.Substring(pos, bestMatch.Index - pos), "PlainText");

                if (winner.Scope == "String")
                    EmitString(formatted, bestMatch.Value);
                else
                    AddSpan(formatted, bestMatch.Value, winner.Scope);

                pos = bestMatch.Index + bestMatch.Length;
                if (bestMatch.Length == 0) pos++;
            }

            return formatted;
        }

        private FormattedString PlainText(string code)
        {
            var fs = new FormattedString();
            AddSpan(fs, code, "PlainText");
            return fs;
        }

        private static readonly Regex _stringInternal = new(
            @"(\{[^}]*\})|(\\[\\""'0abfnrtv]|\\u[0-9a-fA-F]{4})",
            RegexOptions.Compiled);

        private void EmitString(FormattedString formatted, string text)
        {
            int last = 0;
            foreach (Match m in _stringInternal.Matches(text))
            {
                if (m.Index > last)
                    AddSpan(formatted, text.Substring(last, m.Index - last), "String");

                if (m.Value.StartsWith("{"))
                {
                    AddSpan(formatted, "{", "Keyword");
                    AddSpan(formatted, m.Value.Substring(1, m.Value.Length - 2), "PlainText");
                    AddSpan(formatted, "}", "Keyword");
                }
                else
                {
                    AddSpan(formatted, m.Value, "Preprocessor");
                }
                last = m.Index + m.Length;
            }
            if (last < text.Length)
                AddSpan(formatted, text.Substring(last), "String");
        }

        private void AddSpan(FormattedString formatted, string text, string scope)
        {
            if (string.IsNullOrEmpty(text)) return;
            var color = Resolve(scope);

            if (formatted.Spans.Count > 0)
            {
                var last = formatted.Spans[formatted.Spans.Count - 1];
                if (last.TextColor == color && last.FontFamily == FontFamily)
                {
                    last.Text += text;
                    return;
                }
            }

            formatted.Spans.Add(new Span
            {
                Text = text,
                TextColor = color,
                FontFamily = FontFamily,
                FontSize = FontSize
            });
        }

        private LanguageDefinition? ResolveLanguage(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            return _languages.FirstOrDefault(l => l.Matches(name));
        }


        private static TokenRule CStyleComments() => new("Comment",
            @"//[^\r\n]*|/\*[\s\S]*?\*/");

        private static TokenRule CStyleStrings() => new("String",
            @"""(?:[^""\\]|\\.)*""|'(?:[^'\\]|\\.)*'");

        private static TokenRule CommonNumbers() => new("Number",
            @"\b(0x[0-9A-Fa-f]+[lLuU]*|0b[01]+[lLuU]*|\d[\d_]*(?:\.\d+)?(?:[eE][+-]?\d+)?[fFdDmMlLuU]*)\b");

        private static TokenRule CommonConstants() => new("Constant",
            @"\b[A-Z_][A-Z0-9_]{2,}\b");

        private static TokenRule CommonMethods() => new("Method",
            @"\b[a-zA-Z_]\w*(?=\s*\()");

        private static TokenRule DotProperties() => new("Property",
            @"(?<=\.)[a-zA-Z_]\w*\b");

        private static TokenRule Keywords(string words,
            RegexOptions opts = RegexOptions.None) =>
            new("Keyword", $@"\b({words})\b", opts);


        private void RegisterLanguages()
        {
            Register(BuildCSharp());
            Register(BuildCpp());
            Register(BuildJava());
            Register(BuildJavaScript("javascript", "js"));
            Register(BuildJavaScript("typescript", "ts"));
            Register(BuildPython());
            Register(BuildGo());
            Register(BuildRust());
            Register(BuildRuby());
            Register(BuildSwift());
            Register(BuildKotlin());
            Register(BuildDart());
            Register(BuildScala());
            Register(BuildPhp());
            Register(BuildObjectiveC());
            Register(BuildSql());
            Register(BuildBash());
            Register(BuildHtml());
            Register(BuildCss());
            Register(BuildJson());
            Register(BuildXml());
        }


        private static LanguageDefinition BuildCSharp()
        {
            var l = new LanguageDefinition("csharp", "cs", "c#");
            l.Rules.Add(CStyleComments());
            l.Rules.Add(new("String",
                @"@""(?:""|[^""])*""|""(?:[^""\\]|\\.)*""|'(?:[^'\\]|\\.)*'"));
            l.Rules.Add(new("Attribute",
                @"\[(?:assembly|module|type|return|param|method|field|property|event)?:?\s*[A-Za-z_]\w*"));
            l.Rules.Add(new("Preprocessor", @"#[^\r\n]*"));
            l.Rules.Add(Keywords(
                "abstract|as|base|bool|break|byte|case|catch|char|checked|class|const|" +
                "continue|decimal|default|delegate|do|double|else|enum|event|explicit|" +
                "extern|false|finally|fixed|float|for|foreach|goto|if|implicit|in|int|" +
                "interface|internal|is|lock|long|namespace|new|null|object|operator|out|" +
                "override|params|private|protected|public|readonly|ref|return|sbyte|" +
                "sealed|short|sizeof|stackalloc|static|string|struct|switch|this|throw|" +
                "true|try|typeof|uint|ulong|unchecked|unsafe|ushort|using|var|virtual|" +
                "void|volatile|while|async|await|dynamic|yield|record|init|with|global|" +
                "file|required|scoped|select|where|group|orderby|join|from|let|into|on|" +
                "by|equals|ascending|descending|partial|get|set|add|remove|value|when"));
            l.Rules.Add(CommonConstants());
            l.Rules.Add(CommonMethods());
            l.Rules.Add(new("Type", @"\b[A-Z][a-zA-Z0-9_]*\b"));
            l.Rules.Add(DotProperties());
            l.Rules.Add(CommonNumbers());
            return l;
        }

        private static LanguageDefinition BuildCpp()
        {
            var l = new LanguageDefinition("cpp", "c++", "c", "cc", "h", "hpp");
            l.Rules.Add(CStyleComments());
            l.Rules.Add(CStyleStrings());
            l.Rules.Add(new("Preprocessor", @"#[^\r\n]*"));
            l.Rules.Add(Keywords(
                "alignas|alignof|and|asm|auto|bitand|bitor|bool|break|case|catch|char|" +
                "class|compl|concept|const|consteval|constexpr|const_cast|continue|" +
                "co_await|co_return|co_yield|decltype|default|delete|do|double|" +
                "dynamic_cast|else|enum|explicit|export|extern|false|float|for|friend|" +
                "goto|if|inline|int|long|mutable|namespace|new|noexcept|not|not_eq|" +
                "nullptr|operator|or|or_eq|private|protected|public|reinterpret_cast|" +
                "requires|return|short|signed|sizeof|static|static_assert|static_cast|" +
                "struct|switch|template|this|thread_local|throw|true|try|typedef|" +
                "typeid|typename|union|unsigned|using|virtual|void|volatile|wchar_t|" +
                "while|xor|xor_eq|override|final"));
            l.Rules.Add(CommonConstants());
            l.Rules.Add(new("Method", @"(?<=::)[a-zA-Z_]\w*(?=\s*\()"));
            l.Rules.Add(CommonMethods());
            l.Rules.Add(new("Type", @"\b[A-Z][a-zA-Z0-9_]*\b"));
            l.Rules.Add(DotProperties());
            l.Rules.Add(CommonNumbers());
            return l;
        }

        private static LanguageDefinition BuildJava()
        {
            var l = new LanguageDefinition("java");
            l.Rules.Add(CStyleComments());
            l.Rules.Add(CStyleStrings());
            l.Rules.Add(new("Attribute", @"@[A-Za-z_]\w*"));
            l.Rules.Add(Keywords(
                "abstract|assert|boolean|break|byte|case|catch|char|class|const|continue|" +
                "default|do|double|else|enum|extends|final|finally|float|for|goto|if|" +
                "implements|import|instanceof|int|interface|long|native|new|null|package|" +
                "private|protected|public|return|short|static|strictfp|super|switch|" +
                "synchronized|this|throw|throws|transient|true|false|try|void|volatile|" +
                "while|record|sealed|permits|yield|var"));
            l.Rules.Add(CommonConstants());
            l.Rules.Add(CommonMethods());
            l.Rules.Add(new("Type", @"\b[A-Z][a-zA-Z0-9_]*\b"));
            l.Rules.Add(DotProperties());
            l.Rules.Add(CommonNumbers());
            return l;
        }

        private static LanguageDefinition BuildJavaScript(string id, params string[] aliases)
        {
            var l = new LanguageDefinition(id, aliases);
            l.Rules.Add(CStyleComments());
            l.Rules.Add(new("String",
                @"`(?:[^`\\]|\\.)*`|""(?:[^""\\]|\\.)*""|'(?:[^'\\]|\\.)*'"));
            l.Rules.Add(new("Attribute", @"@[A-Za-z_]\w*"));
            l.Rules.Add(Keywords(
                "abstract|as|async|await|break|case|catch|class|const|constructor|" +
                "continue|debugger|declare|default|delete|do|else|enum|export|extends|" +
                "false|finally|for|from|function|get|if|implements|import|in|instanceof|" +
                "interface|let|module|namespace|new|null|of|override|package|private|" +
                "protected|public|readonly|return|set|static|super|switch|this|throw|" +
                "true|try|type|typeof|undefined|var|void|while|with|yield|keyof|infer|" +
                "never|unknown|any|string|number|boolean|symbol|object|bigint|satisfies"));
            l.Rules.Add(CommonConstants());
            l.Rules.Add(new("Method", @"\b[a-zA-Z_]\w*(?=\s*(?:=>|\())"));
            l.Rules.Add(new("Type", @"\b[A-Z][a-zA-Z0-9_]*\b"));
            l.Rules.Add(DotProperties());
            l.Rules.Add(CommonNumbers());
            return l;
        }

        private static LanguageDefinition BuildPython()
        {
            var l = new LanguageDefinition("python", "py");
            l.Rules.Add(new("Comment", @"#[^\r\n]*"));
            l.Rules.Add(new("String",
                @"""""""[\s\S]*?""""""|'''[\s\S]*?'''|""(?:[^""\\]|\\.)*""|'(?:[^'\\]|\\.)*'"));
            l.Rules.Add(new("Attribute", @"@[A-Za-z_]\w*"));
            l.Rules.Add(Keywords(
                "and|as|assert|async|await|break|class|continue|def|del|elif|else|" +
                "except|exec|finally|for|from|global|if|import|in|is|lambda|None|" +
                "nonlocal|not|or|pass|print|raise|return|True|False|try|while|with|" +
                "yield|match|case|type"));
            l.Rules.Add(CommonConstants());
            l.Rules.Add(new("Method", @"\bdef\s+[a-zA-Z_]\w*"));
            l.Rules.Add(CommonMethods());
            l.Rules.Add(new("Type", @"\bclass\s+[A-Za-z_]\w*"));
            l.Rules.Add(new("Type", @"\b[A-Z][a-zA-Z0-9_]*\b"));
            l.Rules.Add(new("Property", @"(?<=self\.)[a-zA-Z_]\w*\b"));
            l.Rules.Add(DotProperties());
            l.Rules.Add(CommonNumbers());
            return l;
        }

        private static LanguageDefinition BuildGo()
        {
            var l = new LanguageDefinition("go", "golang");
            l.Rules.Add(CStyleComments());
            l.Rules.Add(new("String", @"`[^`]*`|""(?:[^""\\]|\\.)*"""));
            l.Rules.Add(Keywords(
                "break|case|chan|const|continue|default|defer|else|fallthrough|for|" +
                "func|go|goto|if|import|interface|map|package|range|return|select|" +
                "struct|switch|type|var|nil|true|false|iota|make|new|len|cap|append|" +
                "copy|close|delete|panic|recover|print|println|error|string|int|int8|" +
                "int16|int32|int64|uint|uint8|uint16|uint32|uint64|uintptr|byte|rune|" +
                "float32|float64|complex64|complex128|bool|any"));
            l.Rules.Add(CommonConstants());
            l.Rules.Add(CommonMethods());
            l.Rules.Add(new("Type", @"\b[A-Z][a-zA-Z0-9_]*\b"));
            l.Rules.Add(DotProperties());
            l.Rules.Add(CommonNumbers());
            return l;
        }

        private static LanguageDefinition BuildRust()
        {
            var l = new LanguageDefinition("rust", "rs");
            l.Rules.Add(CStyleComments());
            l.Rules.Add(new("String", @"""(?:[^""\\]|\\.)*"""));
            l.Rules.Add(new("Attribute", @"#!?\[[^\]]*\]"));
            l.Rules.Add(new("Preprocessor", @"\b[a-z_]+!(?=\s*[\(\[{])"));
            l.Rules.Add(Keywords(
                "as|async|await|break|const|continue|crate|dyn|else|enum|extern|false|" +
                "fn|for|if|impl|in|let|loop|match|mod|move|mut|pub|ref|return|self|" +
                "Self|static|struct|super|trait|true|type|union|unsafe|use|where|while|" +
                "str|bool|char|i8|i16|i32|i64|i128|isize|u8|u16|u32|u64|u128|usize|" +
                "f32|f64|String|Vec|Option|Result|Some|None|Ok|Err|Box|Rc|Arc"));
            l.Rules.Add(CommonConstants());
            l.Rules.Add(CommonMethods());
            l.Rules.Add(new("Type", @"\b[A-Z][a-zA-Z0-9_]*\b"));
            l.Rules.Add(DotProperties());
            l.Rules.Add(CommonNumbers());
            return l;
        }

        private static LanguageDefinition BuildRuby()
        {
            var l = new LanguageDefinition("ruby", "rb");
            l.Rules.Add(new("Comment", @"#[^\r\n]*"));
            l.Rules.Add(CStyleStrings());
            l.Rules.Add(Keywords(
                "alias|and|begin|break|case|class|def|defined|do|else|elsif|end|ensure|" +
                "false|for|if|in|module|next|nil|not|or|redo|rescue|retry|return|self|" +
                "super|then|true|undef|unless|until|when|while|yield|attr_accessor|" +
                "attr_reader|attr_writer|include|extend|require|require_relative|raise|" +
                "puts|print|lambda|proc"));
            l.Rules.Add(new("Property", @"@@?[A-Za-z_]\w*"));
            l.Rules.Add(CommonConstants());
            l.Rules.Add(new("Method", @"\bdef\s+[a-zA-Z_]\w*[!?=]?"));
            l.Rules.Add(CommonMethods());
            l.Rules.Add(new("Type", @"\b[A-Z][a-zA-Z0-9_]*\b"));
            l.Rules.Add(DotProperties());
            l.Rules.Add(CommonNumbers());
            return l;
        }

        private static LanguageDefinition BuildSwift()
        {
            var l = new LanguageDefinition("swift");
            l.Rules.Add(CStyleComments());
            l.Rules.Add(new("String", @"""(?:[^""\\]|\\.)*"""));
            l.Rules.Add(new("Attribute", @"@[A-Za-z_]\w*"));
            l.Rules.Add(Keywords(
                "actor|any|as|associatedtype|async|await|break|case|catch|class|continue|" +
                "convenience|default|defer|deinit|didSet|do|dynamic|else|enum|extension|" +
                "fallthrough|false|fileprivate|final|for|func|get|guard|if|import|in|" +
                "indirect|infix|init|inout|internal|is|lazy|let|mutating|nil|nonisolated|" +
                "nonmutating|open|operator|optional|override|postfix|precedencegroup|" +
                "prefix|private|protocol|public|repeat|required|rethrows|return|self|" +
                "set|some|static|struct|subscript|super|switch|throws|throw|true|try|" +
                "typealias|unowned|var|weak|where|while|willSet"));
            l.Rules.Add(CommonConstants());
            l.Rules.Add(CommonMethods());
            l.Rules.Add(new("Type", @"\b[A-Z][a-zA-Z0-9_]*\b"));
            l.Rules.Add(DotProperties());
            l.Rules.Add(CommonNumbers());
            return l;
        }

        private static LanguageDefinition BuildKotlin()
        {
            var l = new LanguageDefinition("kotlin", "kt", "kts");
            l.Rules.Add(CStyleComments());
            l.Rules.Add(new("String",
                @"""""""[\s\S]*?""""""|""(?:[^""\\]|\\.)*"""));
            l.Rules.Add(new("Attribute", @"@[A-Za-z_]\w*"));
            l.Rules.Add(Keywords(
                "abstract|actual|annotation|as|break|by|catch|class|companion|const|" +
                "constructor|continue|crossinline|data|do|else|enum|expect|external|" +
                "false|final|finally|for|fun|get|if|import|in|infix|init|inline|inner|" +
                "interface|internal|is|lateinit|noinline|null|object|open|operator|out|" +
                "override|package|private|protected|public|reified|return|sealed|set|" +
                "super|suspend|tailrec|this|throw|true|try|typealias|val|value|var|" +
                "vararg|when|where|while"));
            l.Rules.Add(CommonConstants());
            l.Rules.Add(CommonMethods());
            l.Rules.Add(new("Type", @"\b[A-Z][a-zA-Z0-9_]*\b"));
            l.Rules.Add(DotProperties());
            l.Rules.Add(CommonNumbers());
            return l;
        }

        private static LanguageDefinition BuildDart()
        {
            var l = new LanguageDefinition("dart");
            l.Rules.Add(CStyleComments());
            l.Rules.Add(CStyleStrings());
            l.Rules.Add(new("Attribute", @"@[A-Za-z_]\w*"));
            l.Rules.Add(Keywords(
                "abstract|as|assert|async|await|base|break|case|catch|class|const|" +
                "continue|covariant|default|deferred|do|dynamic|else|enum|export|" +
                "extends|extension|external|factory|false|final|finally|for|Function|" +
                "get|hide|if|implements|import|in|interface|is|late|library|mixin|new|" +
                "null|on|operator|part|required|rethrow|return|sealed|set|show|static|" +
                "super|switch|sync|this|throw|true|try|typedef|var|void|when|while|" +
                "with|yield"));
            l.Rules.Add(CommonConstants());
            l.Rules.Add(CommonMethods());
            l.Rules.Add(new("Type", @"\b[A-Z][a-zA-Z0-9_]*\b"));
            l.Rules.Add(DotProperties());
            l.Rules.Add(CommonNumbers());
            return l;
        }

        private static LanguageDefinition BuildScala()
        {
            var l = new LanguageDefinition("scala");
            l.Rules.Add(CStyleComments());
            l.Rules.Add(new("String",
                @"""""""[\s\S]*?""""""|""(?:[^""\\]|\\.)*"""));
            l.Rules.Add(new("Attribute", @"@[A-Za-z_]\w*"));
            l.Rules.Add(Keywords(
                "abstract|case|catch|class|def|do|else|enum|export|extends|false|" +
                "final|finally|for|forSome|given|if|implicit|import|lazy|match|new|" +
                "null|object|opaque|open|override|package|private|protected|return|" +
                "sealed|super|then|this|throw|trait|transparent|true|try|type|using|" +
                "val|var|while|with|yield"));
            l.Rules.Add(CommonConstants());
            l.Rules.Add(CommonMethods());
            l.Rules.Add(new("Type", @"\b[A-Z][a-zA-Z0-9_]*\b"));
            l.Rules.Add(DotProperties());
            l.Rules.Add(CommonNumbers());
            return l;
        }

        private static LanguageDefinition BuildPhp()
        {
            var l = new LanguageDefinition("php");
            l.Rules.Add(new("Comment", @"//[^\r\n]*|#[^\r\n]*|/\*[\s\S]*?\*/"));
            l.Rules.Add(CStyleStrings());
            l.Rules.Add(new("Property", @"\$[A-Za-z_]\w*"));
            l.Rules.Add(Keywords(
                "abstract|and|array|as|break|callable|case|catch|class|clone|const|" +
                "continue|declare|default|die|do|echo|else|elseif|empty|enddeclare|" +
                "endfor|endforeach|endif|endswitch|endwhile|enum|eval|exit|extends|" +
                "false|final|finally|fn|for|foreach|function|global|goto|if|implements|" +
                "include|include_once|instanceof|insteadof|interface|isset|list|match|" +
                "namespace|new|null|or|print|private|protected|public|readonly|require|" +
                "require_once|return|static|switch|throw|trait|true|try|unset|use|var|" +
                "void|while|xor|yield"));
            l.Rules.Add(CommonConstants());
            l.Rules.Add(CommonMethods());
            l.Rules.Add(new("Type", @"\b[A-Z][a-zA-Z0-9_]*\b"));
            l.Rules.Add(DotProperties());
            l.Rules.Add(CommonNumbers());
            return l;
        }

        private static LanguageDefinition BuildObjectiveC()
        {
            var l = new LanguageDefinition("objective-c", "objc", "mm");
            l.Rules.Add(CStyleComments());
            l.Rules.Add(new("String",
                @"@""(?:[^""\\]|\\.)*""|""(?:[^""\\]|\\.)*"""));
            l.Rules.Add(new("Preprocessor", @"#[^\r\n]*"));
            l.Rules.Add(Keywords(
                "auto|break|case|char|const|continue|default|do|double|else|enum|" +
                "extern|float|for|goto|if|int|long|nil|NO|null|register|return|self|" +
                "short|signed|sizeof|static|struct|super|switch|typedef|union|unsigned|" +
                "void|volatile|while|YES|nullable|nonnull|strong|weak|assign|copy|" +
                "readonly|readwrite|nonatomic|atomic|IBOutlet|IBAction|" +
                "@interface|@implementation|@end|@property|@synthesize|@dynamic|" +
                "@protocol|@class|@selector|@encode|@try|@catch|@finally|@throw|" +
                "@autoreleasepool|@import|@available"));
            l.Rules.Add(CommonConstants());
            l.Rules.Add(CommonMethods());
            l.Rules.Add(new("Type", @"\b[A-Z][a-zA-Z0-9_]*\b"));
            l.Rules.Add(DotProperties());
            l.Rules.Add(CommonNumbers());
            return l;
        }

        private static LanguageDefinition BuildSql()
        {
            var l = new LanguageDefinition("sql");
            l.Rules.Add(new("Comment", @"--[^\r\n]*|/\*[\s\S]*?\*/"));
            l.Rules.Add(new("String", @"N?'(?:[^'\\]|''|\\.)*'"));
            l.Rules.Add(Keywords(
                "ADD|ALL|ALTER|AND|ANY|AS|ASC|BACKUP|BETWEEN|BY|CASE|CHECK|COLUMN|" +
                "CONSTRAINT|CREATE|CROSS|DATABASE|DEFAULT|DELETE|DESC|DISTINCT|DROP|" +
                "ELSE|END|EXEC|EXISTS|FOREIGN|FROM|FULL|GROUP|HAVING|IN|INDEX|INNER|" +
                "INSERT|INTO|IS|JOIN|KEY|LEFT|LIKE|LIMIT|NOT|NULL|ON|OR|ORDER|OUTER|" +
                "PRIMARY|PROCEDURE|RIGHT|ROWNUM|SELECT|SET|TABLE|TOP|TRUNCATE|UNION|" +
                "UNIQUE|UPDATE|VALUES|VIEW|WHERE|WITH|OVER|PARTITION|RANK|ROW_NUMBER|" +
                "DENSE_RANK|LEAD|LAG|FIRST_VALUE|LAST_VALUE|CAST|CONVERT|COALESCE|" +
                "NULLIF|ISNULL|GETDATE|COUNT|SUM|AVG|MIN|MAX|BEGIN|COMMIT|ROLLBACK|" +
                "TRANSACTION|DECLARE|FETCH|CURSOR|OPEN|CLOSE|DEALLOCATE|TRIGGER|" +
                "AFTER|BEFORE|INSTEAD|REFERENCES|GRANT|REVOKE|DENY",
                RegexOptions.IgnoreCase));
            l.Rules.Add(CommonNumbers());
            return l;
        }

        private static LanguageDefinition BuildBash()
        {
            var l = new LanguageDefinition("bash", "sh", "shell", "zsh");
            l.Rules.Add(new("Comment", @"#[^\r\n]*"));
            l.Rules.Add(new("String",
                @"""(?:[^""\\]|\\.)*""|'[^']*'"));
            l.Rules.Add(new("Property", @"\$\{?[A-Za-z_]\w*\}?"));
            l.Rules.Add(Keywords(
                "if|then|else|elif|fi|for|while|until|do|done|case|esac|in|function|" +
                "return|exit|break|continue|local|export|readonly|unset|shift|set|" +
                "source|alias|echo|printf|read|test|true|false|trap|exec|eval"));
            l.Rules.Add(CommonNumbers());
            return l;
        }

        private static LanguageDefinition BuildHtml()
        {
            var l = new LanguageDefinition("html", "htm");
            l.Rules.Add(new("Comment", @"<!--[\s\S]*?-->"));
            l.Rules.Add(new("String", @"""[^""]*""|'[^']*'"));
            l.Rules.Add(new("Preprocessor", @"<!DOCTYPE[^>]*>"));
            l.Rules.Add(new("Keyword", @"</?\s*[a-zA-Z][a-zA-Z0-9]*"));
            l.Rules.Add(new("Property", @"\b[a-zA-Z-]+(?=\s*=)"));
            return l;
        }

        private static LanguageDefinition BuildCss()
        {
            var l = new LanguageDefinition("css");
            l.Rules.Add(new("Comment", @"/\*[\s\S]*?\*/"));
            l.Rules.Add(new("String", @"""[^""]*""|'[^']*'"));
            l.Rules.Add(new("Attribute", @"@[a-zA-Z-]+"));
            l.Rules.Add(new("Type", @"[.#]?[a-zA-Z][a-zA-Z0-9_-]*(?=\s*\{)"));
            l.Rules.Add(new("Property", @"\b[a-zA-Z-]+(?=\s*:)"));
            l.Rules.Add(new("Number",
                @"\b\d+(?:\.\d+)?(?:px|em|rem|%|vh|vw|pt|cm|mm|in|ex|ch|fr|deg|rad|turn|s|ms)?\b"));
            l.Rules.Add(Keywords(
                "important|inherit|initial|unset|revert|auto|none|normal|bold|italic|" +
                "underline|solid|dashed|dotted|flex|grid|block|inline|absolute|relative|" +
                "fixed|sticky|hidden|visible|center|left|right|top|bottom"));
            return l;
        }

        private static LanguageDefinition BuildJson()
        {
            var l = new LanguageDefinition("json", "jsonc");
            l.Rules.Add(new("Comment", @"//[^\r\n]*|/\*[\s\S]*?\*/"));
            l.Rules.Add(new("Property", @"""(?:[^""\\]|\\.)*""(?=\s*:)"));
            l.Rules.Add(new("String", @"""(?:[^""\\]|\\.)*"""));
            l.Rules.Add(Keywords("true|false|null"));
            l.Rules.Add(CommonNumbers());
            return l;
        }

        private static LanguageDefinition BuildXml()
        {
            var l = new LanguageDefinition("xml", "xaml", "svg", "xhtml");
            l.Rules.Add(new("Comment", @"<!--[\s\S]*?-->"));
            l.Rules.Add(new("String", @"""[^""]*""|'[^']*'"));
            l.Rules.Add(new("Preprocessor", @"<\?[\s\S]*?\?>|<!DOCTYPE[^>]*>"));
            l.Rules.Add(new("Keyword", @"</?\s*[a-zA-Z][a-zA-Z0-9.:_-]*"));
            l.Rules.Add(new("Property", @"\b[a-zA-Z.:_-]+(?=\s*=)"));
            return l;
        }
    }
}