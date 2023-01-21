using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace AltT4SourceGeneratorCore
{
    [Generator(LanguageNames.CSharp)]
    public class IncrementalGenerator : IIncrementalGenerator
    {
        private enum TextKind
        {
            TextContent,
            Code,
            Expression,
        }

        private enum ParseStageTextKind
        {
            TextContent,
            Code,
            Expression,
            Directive,
        }

        private record RegisterSourceOutputArgs(AdditionalText textTemplateText, int number, ILookup<string, AdditionalText> includes, ParseOptions parseOptions, Compilation compilation, string? culture);

        private record TemplateSection(TextKind kind, string file, int lineNumber, int columnNumer, string content);

        private record ParseStateSection(ParseStageTextKind kind, int lineNumber, int columnNumer, string content);

        private const string IncludeFileExtension      = ".ttinc";
        private const string TextTemplateFileExtension = ".sgtt";

        private const string NamespaceName = "GenNamespace";
        private const string ClassName     = "GenClass";
        private const string MethodName    = "Execute";

        private const string TextTemplateAssemblySourceName = "TextTemplateSource.cs";
        private const string TextTemplateAssemblyName = "TextTemplateOnetimeAssembly";

        /// <summary>
        /// /\Afile=&quot;(?&lt;file&gt;.+?)&quot;(?&lt;once&gt;\s+once=&quot;true&quot;)?\s*\Z/
        /// </summary>
        private static Regex s_includeDirectiveRegex = new Regex(@"\Afile=""(?<file>.+?)""(?<once>\s+once=""true"")?\s*\Z");


        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var source = context.AdditionalTextsProvider
                .Where(IsTextTemplate)
                .Combine(context.AdditionalTextsProvider.Collect().Select(ToFileNameLookup))
                .Select(Selecter)
                .Combine(context.ParseOptionsProvider)
                .Combine(context.CompilationProvider)
                .Combine(context.AnalyzerConfigOptionsProvider.Select(ExtractCultureName))
                .Select((v, ct) => new RegisterSourceOutputArgs(v.Left.Left.Left.textTemplateText, v.Left.Left.Left.number, v.Left.Left.Left.includes, v.Left.Left.Right, v.Left.Right, v.Right))
                ;

            context.RegisterSourceOutput(source, DoRegisterSourceOutput);
        }

        private static bool IsTextTemplate(AdditionalText additionalText) => additionalText.Path.EndsWith(TextTemplateFileExtension, StringComparison.OrdinalIgnoreCase);

        private static bool IsIncludeFile(AdditionalText additionalText) => additionalText.Path.EndsWith(IncludeFileExtension, StringComparison.OrdinalIgnoreCase);

        private static string? ExtractCultureName(AnalyzerConfigOptionsProvider options, CancellationToken cancellationToken)
        {
            if (options.GlobalOptions.TryGetValue("build_property.AltT4SourceGeneratorDefaultCulture", out var cultureName))
            {
                return cultureName.Trim();
            }
            else
            {
                return null;
            }
        }

        private (ImmutableArray<(AdditionalText additionalText, string fileName)> textTemplates, ILookup<string, AdditionalText> includeFileLookup) ToFileNameLookup(ImmutableArray<AdditionalText> args, CancellationToken cancellationToken)
        {
            var textTemplates = args
                .Where(IsTextTemplate)
                .Select(textTemplateFile => (textTemplateFile, Path.GetFileName(textTemplateFile.Path)))
                .ToImmutableArray();

            var includeFileLookup = args
                .Where(IsIncludeFile)
                .Select(includeFile => (includeFile, fileName: Path.GetFileName(includeFile.Path)))
                .ToLookup(v => v.fileName, v => v.includeFile);

            return (textTemplates, includeFileLookup);
        }

        private (AdditionalText textTemplateText, int number, ILookup<string, AdditionalText> includes) Selecter((AdditionalText textTemplateText, (ImmutableArray<(AdditionalText additionalText, string fileName)> textTemplates, ILookup<string, AdditionalText> includeFileLookup) relationFiles) args, CancellationToken cancellationToken)
        {
            var fileName = Path.GetFileName(args.textTemplateText.Path);

            int count = 0;
            int number = 0;
            foreach (var sameNameItem in args.relationFiles.textTemplates.Where(v => v.fileName == fileName))
            {
                count++;

                if (sameNameItem.additionalText.Path == args.textTemplateText.Path)
                {
                    number = count;
                }
            }

            if (count <= 1)
            {
                return (args.textTemplateText, 0, args.relationFiles.includeFileLookup);
            }
            else
            {
                return (args.textTemplateText, number, args.relationFiles.includeFileLookup);
            }
        }

        private void DoRegisterSourceOutput(SourceProductionContext context, RegisterSourceOutputArgs args)
        {
            var templateSourceText = args.textTemplateText.GetText(context.CancellationToken);

            if (templateSourceText is null)
            {
                return;
            }

            var templateTextSections = new List<TemplateSection>();
            var inclduedSetForCyclicIncludeCheck = ImmutableHashSet<AdditionalText>.Empty;
            var inclduedSetForOnceCheck = new HashSet<AdditionalText>();
            var invalidDirectiveBuilder = new StringBuilder();
            var genClassSourceBuilder = new StringBuilder();

            WriteHeadContentsAndExtractTemplateSections(
                templateSourceText,
                args.textTemplateText.Path,
                templateTextSections,
                inclduedSetForCyclicIncludeCheck,
                inclduedSetForOnceCheck,
                args.includes,
                invalidDirectiveBuilder,
                genClassSourceBuilder,
                out var appendReferenceAssemblies,
                out var appendGeneraterSource
                );

            var nameWithoutExtension = Path.GetFileNameWithoutExtension(args.textTemplateText.Path);

            if (invalidDirectiveBuilder.Length > 0)
            {
                context.AddSource($"{nameWithoutExtension}.cs", invalidDirectiveBuilder.ToString());
                return;
            }

            WriteMainContent(templateTextSections, genClassSourceBuilder, args.culture);

            var memoryStream = new MemoryStream();

            var genClassSourceText = genClassSourceBuilder.ToString();

            var (syntaxTree, emitResult, references) = CompileTextTemplateAssembly(context, args.parseOptions, memoryStream, genClassSourceText);

            string sourceText;

            if (!emitResult.Success)
            {
                sourceText = MakeCompileFailedFallbackSourceCodeAndReportRedirectedDiagnostic(context, args, genClassSourceText, syntaxTree, emitResult);
            }
            else
            {
                memoryStream.Position = 0;
                sourceText = MakeTextTemplateOutputSourceCode(memoryStream, context.CancellationToken);

                if (appendReferenceAssemblies || appendGeneraterSource)
                {
                    StringBuilder stringBuilder = new StringBuilder(sourceText.Length + genClassSourceText.Length + genClassSourceText.Count(ch => ch == '\n') * 3 + 1000);

                    stringBuilder.AppendLine(sourceText);

                    if (appendReferenceAssemblies)
                    {
                        stringBuilder.AppendLine();
                        stringBuilder.AppendLine("// *** Above source is compiled with following reference assemblies. ***");
                        stringBuilder.AppendLine("//");

                        foreach (var refAssembly in references)
                        {
                            stringBuilder.AppendLine($"// {refAssembly.FullName}");
                        }
                    }
                        
                    if (appendGeneraterSource)
                    {
                        stringBuilder.AppendLine();
                        stringBuilder.AppendLine("// *** Above source is generated from following code. ***");
                        stringBuilder.AppendLine("//");
                        AppendAsCommentedText(stringBuilder, genClassSourceText);
                    }

                    sourceText = stringBuilder.ToString();
                }

            }

            if (args.number == 0)
            {
                context.AddSource($"{nameWithoutExtension}.cs", sourceText);
            }
            else
            {
                context.AddSource($"{nameWithoutExtension}.{args.number}.cs", sourceText);
            }

            return;


            // ローカル関数
            static void WriteHeadContentsAndExtractTemplateSections(
                SourceText templateSourceText,
                string file,
                List<TemplateSection> templateTextSections,
                ImmutableHashSet<AdditionalText> inclduedSetForCyclicIncludeCheck,
                HashSet<AdditionalText> inclduedSetForOnceCheck,
                ILookup<string, AdditionalText> includes,
                StringBuilder invalidDirectiveBuilder,
                StringBuilder genClassSourceBuilder,
                out bool appendReferenceAssemblies,
                out bool appendGeneraterSource
                )
            {
                if (!Path.IsPathRooted(file)) throw new ArgumentException(null, nameof(file));

                appendReferenceAssemblies = false;
                appendGeneraterSource = false;

                foreach (var section in ToInlineContents(templateSourceText))
                {
                    if (section.kind != ParseStageTextKind.Directive)
                    {
                        // 地のテキスト,式(<#= #>),コード(<# #>)

                        var kind = section.kind switch
                        {
                            ParseStageTextKind.TextContent => TextKind.TextContent,
                            ParseStageTextKind.Expression => TextKind.Expression,
                            ParseStageTextKind.Code => TextKind.Code,
                            _ => throw new InvalidOperationException(),
                        };

                        templateTextSections.Add(new(kind, file, section.lineNumber, section.columnNumer, section.content));

                        continue;
                    }

                    // ディレクティブ(<#@ #>)の内容を処理
                    var directiveContent = section.content.AsSpan().TrimStart(' ');

                    if (directiveContent.IsEmpty)
                    {
                        WriteInvalidDirectiveComment(genClassSourceBuilder, file, section, "UnknownDirective");
                        continue;
                    }

                    var directiveFirstTokenEnd = directiveContent.IndexOf(' ');

                    var directiveFirstToken = directiveContent.Slice(0, directiveFirstTokenEnd);

                    var directiveArgsContent = directiveContent.Slice(directiveFirstTokenEnd).Trim(' ');

                    if (directiveFirstToken.SequenceEqual("import".AsSpan()))
                    {
                        HandleImportDirective(directiveArgsContent, file, section, invalidDirectiveBuilder, genClassSourceBuilder);
                    }
                    else if (directiveFirstToken.SequenceEqual("include".AsSpan()))
                    {
                        HandleIncludeDirective(
                            directiveArgsContent,
                            file,
                            section,
                            templateTextSections,
                            inclduedSetForCyclicIncludeCheck,
                            inclduedSetForOnceCheck,
                            includes,
                            invalidDirectiveBuilder,
                            genClassSourceBuilder,
                            ref appendReferenceAssemblies,
                            ref appendGeneraterSource
                            );
                    }
                    else if (directiveFirstToken.SequenceEqual("AppendReferenceAssemblies".AsSpan()))
                    {
                        appendReferenceAssemblies = true;
                    }
                    else if (directiveFirstToken.SequenceEqual("AppendGeneraterSource".AsSpan()))
                    {
                        appendGeneraterSource = true;
                    }
                    else
                    {
                        WriteInvalidDirectiveComment(invalidDirectiveBuilder, file, section, "UnknownDirective");
                    }
                }
            }

            // ローカル関数
            static void HandleImportDirective(ReadOnlySpan<char> directiveArgsContent, string file, ParseStateSection section, StringBuilder invalidDirectiveBuilder, StringBuilder genClassSourceBuilder)
            {
                // <#@ import="*" #>の処理
                // テンプレートから生成するソースにusing文を追加する

                var importArgsContent = directiveArgsContent;

                var argPrefix = @"namespace=""";
                var argSuffix = @"""";

                if (!importArgsContent.StartsWith(argPrefix.AsSpan()) || !importArgsContent.EndsWith(argSuffix.AsSpan()))
                {
                    WriteInvalidDirectiveComment(invalidDirectiveBuilder, file, section, "InvalidImportDirective");
                    return;
                }

                var usingContent = importArgsContent.Slice(argPrefix.Length, importArgsContent.Length - argPrefix.Length - argSuffix.Length).Trim(' ');

                // using Xxx;の書き出し
#if NETFRAMEWORK
                genClassSourceBuilder.AppendLine($"using {usingContent.ToString()}; // from import directive of {file}({section.lineNumber},{section.columnNumer})");
#else
                genClassSourceBuilder.AppendLine($"using {usingContent}; // from import directive of {file}({section.lineNumber},{section.columnNumer})");
#endif
            }


            // ローカル関数
            static void HandleIncludeDirective(
                ReadOnlySpan<char> directiveArgsContent,
                string file,
                ParseStateSection section,
                List<TemplateSection> templateTextSections,
                ImmutableHashSet<AdditionalText> inclduedSetForCyclicIncludeCheck,
                HashSet<AdditionalText> inclduedSetForOnceCheck,
                ILookup<string, AdditionalText> includes,
                StringBuilder invalidDirectiveBuilder,
                StringBuilder genClassSourceBuilder,
                ref bool appendReferenceAssemblies,
                ref bool appendGeneraterSource
                )
            {
                // <#@ include="*" [once="true"] #>の処理
                // テンプレートから生成するソースにinclude指定されたテンプレートの内容を挿入する

                var includeArgsContent = directiveArgsContent;

                if (includeArgsContent.IsEmpty)
                {
                    WriteInvalidDirectiveComment(invalidDirectiveBuilder, file, section, "InvalidIncludeDirective");
                    return;
                }

                var match = s_includeDirectiveRegex.Match(includeArgsContent.ToString());

                if (!match.Success)
                {
                    WriteInvalidDirectiveComment(invalidDirectiveBuilder, file, section, "InvalidIncludeDirective");
                    return;
                }

                var includeFileName = match.Groups["file"].Value;
                var once = match.Groups["once"].Success;

                if (!includeFileName.EndsWith(IncludeFileExtension))
                {
                    WriteInvalidDirectiveComment(invalidDirectiveBuilder, file, section, $@"UnsupportedExtension");
                    return;
                }

                if (!includes.Contains(includeFileName))
                {
                    WriteInvalidDirectiveComment(invalidDirectiveBuilder, file, section, $@"IncludeFileNotFound");
                    return;
                }
                var candidateIncludeFiles = includes[includeFileName];

                if (candidateIncludeFiles.Count() > 1)
                {
                    WriteInvalidDirectiveComment(invalidDirectiveBuilder, file, section, $@"IncludeFileDuplicated");
                    return;
                }

                var includeFile = candidateIncludeFiles.First();

                var isFirstIncludeFile = inclduedSetForOnceCheck.Add(includeFile);

                if (once && !isFirstIncludeFile)
                {
                    // すでに別の個所でinclude済み
                    return;
                }

                var nextInclduedSetForCyclicIncludeCheck = inclduedSetForCyclicIncludeCheck.Add(includeFile);

                if (nextInclduedSetForCyclicIncludeCheck == inclduedSetForCyclicIncludeCheck)
                {
                    WriteInvalidDirectiveComment(invalidDirectiveBuilder, file, section, $@"CyclicInclude");
                    return;
                }

                var includeFileSourceText = includeFile.GetText();
                if (includeFileSourceText is null)
                {
                    WriteInvalidDirectiveComment(invalidDirectiveBuilder, file, section, $@"MissingSourceText");
                    return;
                }

                try
                {
                    WriteHeadContentsAndExtractTemplateSections(
                        includeFileSourceText,
                        includeFile.Path,
                        templateTextSections,
                        nextInclduedSetForCyclicIncludeCheck,
                        inclduedSetForOnceCheck,
                        includes,
                        invalidDirectiveBuilder,
                        genClassSourceBuilder,
                        out var includeAppendReferenceAssemblies,
                        out var includeAppendGeneraterSource
                        );
                    appendReferenceAssemblies = appendReferenceAssemblies || includeAppendReferenceAssemblies;
                    appendGeneraterSource = appendGeneraterSource || includeAppendGeneraterSource;
                    return;
                }
                catch (FileNotFoundException)
                {
                    WriteInvalidDirectiveComment(invalidDirectiveBuilder, file, section, $@"InvalidIncludeDirective ""{includeFileName}"" is not found.");
                    return;
                }
                catch (IOException)
                {
                    WriteInvalidDirectiveComment(invalidDirectiveBuilder, file, section, $@"InvalidIncludeDirective io error.");
                    return;
                }
                catch (UnauthorizedAccessException)
                {
                    WriteInvalidDirectiveComment(invalidDirectiveBuilder, file, section, $@"InvalidIncludeDirective permission error.");
                    return;
                }
            }

            // ローカル関数
            static void WriteMainContent(List<TemplateSection> templateTextSections, StringBuilder genClassSourceBuilder, string? colutureName)
            {
                genClassSourceBuilder.AppendLine($"using System; // default");
                genClassSourceBuilder.AppendLine($"using System.Collections.Generic; // default");
                genClassSourceBuilder.AppendLine($"using System.Collections.Immutable; // default");
                genClassSourceBuilder.AppendLine($"");
                genClassSourceBuilder.AppendLine($"namespace {NamespaceName} {{");
                genClassSourceBuilder.AppendLine($"    public class {ClassName} {{");
                genClassSourceBuilder.AppendLine($"        public string {MethodName}() {{");
                genClassSourceBuilder.AppendLine($"            var builder = new global::System.Text.StringBuilder();");
                if (string.IsNullOrWhiteSpace(colutureName))
                {
                    genClassSourceBuilder.AppendLine("            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;");
                    genClassSourceBuilder.AppendLine("            System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.InvariantCulture;");
                }
                else
                {
                    genClassSourceBuilder.AppendLine($@"            {{");
                    genClassSourceBuilder.AppendLine($@"                System.Globalization.CultureInfo culture;");
                    genClassSourceBuilder.AppendLine($@"                try {{");
                    genClassSourceBuilder.AppendLine($@"                    culture = System.Globalization.CultureInfo.GetCultureInfo(""{colutureName}"");");
                    genClassSourceBuilder.AppendLine($@"                }}");
                    genClassSourceBuilder.AppendLine($@"                catch {{");
                    genClassSourceBuilder.AppendLine($@"                    culture = null;");
                    genClassSourceBuilder.AppendLine($@"                }}");
                    genClassSourceBuilder.AppendLine($@"                if (culture is null) {{");
                    genClassSourceBuilder.AppendLine($@"                    System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;");
                    genClassSourceBuilder.AppendLine($@"                    System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.InvariantCulture;");
                    genClassSourceBuilder.AppendLine($@"                    builder.AppendLine(""#warning The valid culture could not get from {colutureName}. This soruce is generated using InvalidCulture."");");
                    genClassSourceBuilder.AppendLine($@"                }}");
                    genClassSourceBuilder.AppendLine($@"                else {{");
                    genClassSourceBuilder.AppendLine($@"                    System.Globalization.CultureInfo.CurrentCulture = culture;");
                    genClassSourceBuilder.AppendLine($@"                    System.Globalization.CultureInfo.CurrentUICulture = culture;");
                    genClassSourceBuilder.AppendLine($@"                }}");
                    genClassSourceBuilder.AppendLine($@"            }}");
                }
                foreach (var templateTextSection in templateTextSections)
                {
                    switch (templateTextSection.kind)
                    {
                        case TextKind.Expression:
                            genClassSourceBuilder.AppendLine();
                            genClassSourceBuilder.AppendLine("#line default");
                            genClassSourceBuilder.AppendLine("            builder.Append((");
                            genClassSourceBuilder.AppendLine($@"#line {templateTextSection.lineNumber} ""{templateTextSection.file}""");
                            for (int i = 0; i < templateTextSection.columnNumer; i++) genClassSourceBuilder.Append(' '); // エラーの列位置を元のテキストに合わせるためのパディング
                            genClassSourceBuilder.AppendLine(templateTextSection.content);
                            genClassSourceBuilder.AppendLine("#line default");
                            genClassSourceBuilder.AppendLine(").ToString());");
                            break;
                        case TextKind.Code:
                            genClassSourceBuilder.AppendLine();
                            genClassSourceBuilder.AppendLine($@"#line {templateTextSection.lineNumber} ""{templateTextSection.file}""");
                            for (int i = 0; i < templateTextSection.columnNumer; i++) genClassSourceBuilder.Append(' '); // エラーの列位置を元のテキストに合わせるためのパディング
                            genClassSourceBuilder.Append(templateTextSection.content);
                            break;
                        case TextKind.TextContent:
                            genClassSourceBuilder.AppendLine();
                            genClassSourceBuilder.AppendLine("#line default");
                            genClassSourceBuilder.AppendLine(@$"            builder.Append(@""{templateTextSection.content}"");");
                            break;
                        default:
                            Debug.Fail("未実装");
                            break;

                    }
                }
                genClassSourceBuilder.AppendLine();
                genClassSourceBuilder.AppendLine("#line default");
                genClassSourceBuilder.AppendLine($"            return builder.ToString();");
                genClassSourceBuilder.AppendLine($"        }}");
                genClassSourceBuilder.AppendLine($"        static void Main(string[] args) {{");
                genClassSourceBuilder.AppendLine($"            global::System.IO.File.WriteAllText(args[0], new {ClassName}().{MethodName}());");
                genClassSourceBuilder.AppendLine($"        }}");
                genClassSourceBuilder.AppendLine($"    }}");
                genClassSourceBuilder.AppendLine($"}}");
            }

            // ローカル関数
            static void WriteInvalidDirectiveComment(StringBuilder stringBuilder, string file, ParseStateSection directive, string label)
            {
                stringBuilder.AppendLine($@"// {file}({directive.lineNumber},{directive.columnNumer + 1}) [{label}]: {Regex.Replace(directive.content, @"\r?\n", _ => " ").Trim()}");
            }

            // ローカル関数
            static IEnumerable<ParseStateSection> ToInlineContents(SourceText sourceText)
            {
                const int FirstLineNumber = 1;
                const int FirstColumnNumber = 0;

                var current = ParseStageTextKind.TextContent;
                var buffer = new StringBuilder();
                var lineNumber = FirstLineNumber;
                var columnNumer = FirstColumnNumber;
                var sectionBeginLineNumber = lineNumber;
                var sectionBeginColumnNumber = columnNumer;
                string content;

                for (int i = 0; i < sourceText.Length; i++)
                {
                    var ch = sourceText[i];

                    if (current == ParseStageTextKind.TextContent)
                    {
                        if (ch == '<' && i + 1 < sourceText.Length && sourceText[i + 1] == '#' && HandleTagLeadingEscapeChars(buffer))
                        {
                            if (TryYieldReturn(buffer, out content))
                            {
                                yield return new (current, sectionBeginLineNumber, sectionBeginColumnNumber, content);
                            }

                            if (i + 2 < sourceText.Length && sourceText[i + 2] == '=')
                            {
                                current = ParseStageTextKind.Expression;
                                sectionBeginLineNumber = lineNumber;
                                sectionBeginColumnNumber = columnNumer;
                                i = i + 2;
                            }
                            else if (i + 2 < sourceText.Length && sourceText[i + 2] == '@')
                            {
                                current = ParseStageTextKind.Directive;
                                sectionBeginLineNumber = lineNumber;
                                sectionBeginColumnNumber = columnNumer;
                                i = i + 2;
                            }
                            else
                            {
                                current = ParseStageTextKind.Code;
                                sectionBeginLineNumber = lineNumber;
                                sectionBeginColumnNumber = columnNumer;
                                i = i + 1;
                            }
                        }
                        else
                        {
                            if (ch == '\"')
                            {
                                buffer.Append("\"\"");
                            }
                            else
                            {
                                buffer.Append(ch);
                            }
                        }
                    }
                    else
                    {
                        if (ch == '#' && i + 1 < sourceText.Length && sourceText[i + 1] == '>' && HandleTagLeadingEscapeChars(buffer))
                        {
                            if (TryYieldReturn(buffer, out content))
                            {
                                yield return new (current, sectionBeginLineNumber, sectionBeginColumnNumber, content);
                            }

                            current = ParseStageTextKind.TextContent;
                            sectionBeginLineNumber = lineNumber;
                            sectionBeginColumnNumber = columnNumer;
                            i++;
                            continue;
                        }
                        else
                        {
                            buffer.Append(ch);
                        }
                    }

                    if (ch == '\n')
                    {
                        lineNumber++;
                        columnNumer = FirstColumnNumber;
                    }
                    else
                    {
                        columnNumer++;
                    }
                }

                if (TryYieldReturn(buffer, out content))
                {
                    yield return new (current, sectionBeginLineNumber, sectionBeginColumnNumber, content);
                }
            }

            // ローカル関数
            static bool HandleTagLeadingEscapeChars(StringBuilder buffer)
            {
                int count = 0;
                for (int i = 0; i < buffer.Length; i++)
                {
                    if (buffer[buffer.Length - 1 - i] != '\\')
                    {
                        break;
                    }
                    count++;
                }

                // タグの直前にある１つ以上の連続する/は//を一つの/に置き換える。/の数が奇数個の場合余った/はそのまま残す。
                // /     -> /
                // //    -> /
                // ///   -> //
                // ////  -> //
                // ///// -> ///
                // ...以下同様...
                var removeCount = count / 2;
                buffer.Remove(buffer.Length - removeCount, removeCount);

                if (count % 2 == 0)
                {
                    // 直前の\が元々偶数個だった場合はタグは有効なものとして機能する
                    return true;
                }
                else
                {
                    // 直前の\が元々奇数個だった場合はタグは無効化され通常のテキストの一部として扱われる
                    return false;
                }
            }

            // ローカル関数
            static bool TryYieldReturn(StringBuilder buffer, out string content)
            {
                if (buffer.Length > 0)
                {
                    content = buffer.ToString();
                    buffer.Clear();
                    return true;
                }
                content = "";
                return false;
            }

            // ローカル関数
            static (SyntaxTree, EmitResult, Assembly[]) CompileTextTemplateAssembly(
                SourceProductionContext context,
                ParseOptions inheriteParseOptions,
                MemoryStream memoryStream,
                string genClassSourceText
                )
            {
                var syntaxTree = CSharpSyntaxTree.ParseText(
                    genClassSourceText,
                    inheriteParseOptions as CSharpParseOptions,
                    TextTemplateAssemblySourceName,
                    null,
                    context.CancellationToken
                    );

                var compilationOptions = new CSharpCompilationOptions(
                    outputKind: OutputKind.DynamicallyLinkedLibrary,
                    reportSuppressedDiagnostics: false,
                    moduleName: null,
                    mainTypeName: null,
                    scriptClassName: null,
                    usings: null,
                    optimizationLevel: OptimizationLevel.Release,
                    checkOverflow: false,
                    allowUnsafe: false,
                    cryptoKeyContainer: null,
                    cryptoKeyFile: null,
                    cryptoPublicKey: ImmutableArray<byte>.Empty,
                    delaySign: null,
                    platform: Platform.AnyCpu,
                    generalDiagnosticOption: ReportDiagnostic.Default,
                    warningLevel: 0,
                    specificDiagnosticOptions: null,
                    concurrentBuild: true,
                    deterministic: true,
                    xmlReferenceResolver: null,
                    sourceReferenceResolver: null,
                    metadataReferenceResolver: null,
                    assemblyIdentityComparer: null,
                    strongNameProvider: null,
                    publicSign: false,
                    metadataImportOptions: MetadataImportOptions.All
                    );

#if NETFRAMEWORK
            compilationOptions = compilationOptions
                .WithOutputKind(OutputKind.WindowsApplication)
                .WithMainTypeName($"{GenClassInfo.NamespaceName}.{GenClassInfo.ClassName}");
#endif

                // mscorlib(NET Framework), System.Private.CoreLib(.NET), System.xxx
                var referenceSourceAssemblies = AppDomain.CurrentDomain.GetAssemblies()
                    .Where(v => v.GetName().Name == "mscorlib" || v.GetName().Name?.StartsWith("System.") == true)
                    .Where(v => !v.IsDynamic && File.Exists(v.Location))
                    .ToArray();

                var references = referenceSourceAssemblies.Select(v => MetadataReference.CreateFromFile(v.Location));

                var compilation = CSharpCompilation.Create(TextTemplateAssemblyName, new[] { syntaxTree }, references, compilationOptions);

                var emitOptions = new EmitOptions();

                var emitResult = compilation.Emit(memoryStream, null, null, null, null, emitOptions, null, null, null, context.CancellationToken);

                return (syntaxTree, emitResult, referenceSourceAssemblies);
            }

            // ローカル関数
            static string MakeCompileFailedFallbackSourceCodeAndReportRedirectedDiagnostic(SourceProductionContext context, RegisterSourceOutputArgs args, string genClassSourceText, SyntaxTree syntaxTree, EmitResult emitResult)
            {
                string sourceStringText;
                var stringBuilder = new StringBuilder();
                AppendAsCommentedText(stringBuilder, genClassSourceText);

                var textTemplateFileName = Path.GetFileName(args.textTemplateText.Path);

                stringBuilder.AppendLine();
                stringBuilder.AppendLine($"#error {textTemplateFileName}から生成した出力アセンブリのコンパイルに失敗しました");
                stringBuilder.AppendLine("#if false");

                SourceText? sourceText = null;
                foreach (var origianlDiagnostic in emitResult.Diagnostics)
                {
                    Diagnostic diagnostic;

                    if (origianlDiagnostic.Location.SourceTree != syntaxTree)
                    {
                        stringBuilder.AppendLine(origianlDiagnostic.ToString());
                    }

                    sourceText ??= SourceText.From(stringBuilder.ToString());

                    var originalLineSpan = syntaxTree.GetLineSpan(origianlDiagnostic.Location.SourceSpan);

                    var redirectedTextSpan = new TextSpan(
                        sourceText.Lines[originalLineSpan.Span.Start.Line].Start + originalLineSpan.Span.Start.Character,
                        origianlDiagnostic.Location.SourceSpan.Length
                        );

                    var redirectedLineSpan = new LinePositionSpan(
                        new LinePosition(originalLineSpan.StartLinePosition.Line, originalLineSpan.StartLinePosition.Character + 3),
                        new LinePosition(originalLineSpan.EndLinePosition.Line, originalLineSpan.EndLinePosition.Character + 3)
                        );

                    diagnostic = Diagnostic.Create(
                        origianlDiagnostic.Id,
                        origianlDiagnostic.Descriptor.Category,
                        origianlDiagnostic.GetMessage(),
                        origianlDiagnostic.Severity,
                        origianlDiagnostic.DefaultSeverity,
                        true,
                        origianlDiagnostic.WarningLevel,
                        origianlDiagnostic.Descriptor.Title,
                        origianlDiagnostic.Descriptor.Description,
                        origianlDiagnostic.Descriptor.HelpLinkUri,
                        Location.Create(syntaxTree.FilePath, redirectedTextSpan, redirectedLineSpan)
                        );

                    stringBuilder.AppendLine(diagnostic.ToString());
                }

                stringBuilder.AppendLine("#endif");

                sourceStringText = stringBuilder.ToString();

                foreach (var origianlDiagnostic in emitResult.Diagnostics)
                {
                    var mappedLineSpan = origianlDiagnostic.Location.GetMappedLineSpan();

                    Diagnostic diagnostic;

                    if (mappedLineSpan.HasMappedPath && mappedLineSpan.Path == args.textTemplateText.Path)
                    {
                        TextSpan redirectedTextSpan = default;
                        var text = args.textTemplateText.GetText(context.CancellationToken);
                        if (text is not null && mappedLineSpan.Span.Start.Line < text.Lines.Count)
                        {
                            redirectedTextSpan = new TextSpan(text.Lines[mappedLineSpan.Span.Start.Line].Start + mappedLineSpan.Span.Start.Character, origianlDiagnostic.Location.SourceSpan.Length);
                        }

                        diagnostic = Diagnostic.Create(
                            origianlDiagnostic.Id,
                            origianlDiagnostic.Descriptor.Category,
                            origianlDiagnostic.GetMessage(),
                            origianlDiagnostic.Severity,
                            origianlDiagnostic.DefaultSeverity,
                            true,
                            origianlDiagnostic.WarningLevel,
                            origianlDiagnostic.Descriptor.Title,
                            origianlDiagnostic.Descriptor.Description,
                            origianlDiagnostic.Descriptor.HelpLinkUri,
                            Location.Create(args.textTemplateText.Path, redirectedTextSpan, mappedLineSpan.Span)
                            );
                    }
                    else
                    {
                        sourceText ??= SourceText.From(stringBuilder.ToString());

                        diagnostic = Diagnostic.Create(
                            $"SGTT_{origianlDiagnostic.Id}",
                            origianlDiagnostic.Descriptor.Category,
                            $"TemplateCompile{origianlDiagnostic.Severity} {origianlDiagnostic.GetMessage()}",
                            origianlDiagnostic.Severity,
                            origianlDiagnostic.DefaultSeverity,
                            true,
                            origianlDiagnostic.WarningLevel,
                            origianlDiagnostic.Descriptor.Title,
                            origianlDiagnostic.Descriptor.Description,
                            origianlDiagnostic.Descriptor.HelpLinkUri,
                            Location.Create(args.textTemplateText.Path, default, default)
                            );
                    }
                    context.ReportDiagnostic(diagnostic);
                }

                return sourceStringText;
            }

            // ローカル関数
            static string MakeTextTemplateOutputSourceCode(MemoryStream memoryStream, CancellationToken cancellationToken)
            {
                string sourceStringText;
                try
                {
#if NETFRAMEWORK
                    var tempAssembly = Path.GetTempFileName();
                    var tempSource = Path.GetTempFileName();

                    try
                    {
                        var appDomain = AppDomain.CreateDomain("AltT4SourceGenerator.Temporary", null, AppDomain.CurrentDomain.SetupInformation);

                        try
                        {
                            var sourceTextTask = Task.Run(() =>
                            {
                                var rawAssembly = memoryStream.ToArray();

                                cancellationToken.ThrowIfCancellationRequested();

                                using (var fileStream = new FileStream(tempAssembly, FileMode.Create, FileAccess.Write, FileShare.None))
                                {
                                    fileStream.Write(rawAssembly, 0, rawAssembly.Length);
                                }
                                cancellationToken.ThrowIfCancellationRequested();

                                appDomain.ExecuteAssembly(tempAssembly, new[] { tempSource });

                                cancellationToken.ThrowIfCancellationRequested();

                                var sourceText = File.ReadAllText(tempSource);

                                return sourceText;
                            }, cancellationToken);

                            sourceTextTask.Wait(cancellationToken);

                            sourceStringText = sourceTextTask.Result;
                        }
                        finally
                        {
                            AppDomain.Unload(appDomain);
                        }
                    }
                    finally
                    {
                        File.Delete(tempAssembly);
                        File.Delete(tempSource);
                    }
#else
                    var assemblyLoadContext = new System.Runtime.Loader.AssemblyLoadContext("AltT4SourceGenerator.Temporary", isCollectible: true);
                    try
                    {
                        var assembly = assemblyLoadContext.LoadFromStream(memoryStream);

                        cancellationToken.ThrowIfCancellationRequested();

                        var genClassType = assembly.GetType($"{NamespaceName}.{ClassName}")!;

                        var genClassInstance = Activator.CreateInstance(genClassType);

                        sourceStringText = (string)genClassType.GetMethod(MethodName)!.Invoke(genClassInstance, null)!;
                    }
                    finally
                    {
                        assemblyLoadContext.Unload();
                    }
#endif
                }
                catch (Exception ex)
                {
                    var stringBuilder = new StringBuilder();

                    foreach (var line in Regex.Split(ex.ToString(), @"\r?\n"))
                    {
                        stringBuilder.AppendLine($"// {line}");
                    }

                    sourceStringText = stringBuilder.ToString();
                }

                return sourceStringText;
            }

            // ローカル関数
            static void AppendAsCommentedText(StringBuilder stringBuilder, string text)
            {
                if (string.IsNullOrEmpty(text)) return;

                if (stringBuilder.Length > 0 && stringBuilder[stringBuilder.Length - 1] != '\n')
                {
                    stringBuilder.AppendLine();
                }
                stringBuilder.Append("// ");
                foreach (var ch in text)
                {
                    stringBuilder.Append(ch);
                    if (ch == '\n')
                    {
                        stringBuilder.Append("// ");
                    }
                }
            }
        }
    }
}
