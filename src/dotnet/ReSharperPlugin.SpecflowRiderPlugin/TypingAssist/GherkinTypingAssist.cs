using System;
using System.Text;
using JetBrains.Annotations;
using JetBrains.Application.CommandProcessing;
using JetBrains.Application.Settings;
using JetBrains.Application.UI.ActionSystem.Text;
using JetBrains.Diagnostics;
using JetBrains.Lifetimes;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Feature.Services.CodeCompletion;
using JetBrains.ReSharper.Feature.Services.Options;
using JetBrains.ReSharper.Feature.Services.StructuralRemove;
using JetBrains.ReSharper.Feature.Services.TypingAssist;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.CachingLexers;
using JetBrains.ReSharper.Psi.CodeStyle;
using JetBrains.ReSharper.Psi.Format;
using JetBrains.ReSharper.Psi.Parsing;
using JetBrains.TextControl;
using JetBrains.Util;
using ReSharperPlugin.SpecflowRiderPlugin.Formatting;
using ReSharperPlugin.SpecflowRiderPlugin.Psi;

namespace ReSharperPlugin.SpecflowRiderPlugin.TypingAssist
{
    [SolutionComponent]
    public class GherkinTypingAssist : TypingAssistLanguageBase<GherkinLanguage>, ITypingHandler
    {
        public GherkinTypingAssist(
            Lifetime lifetime,
            [NotNull] ISolution solution,
            [NotNull] ISettingsStore settingsStore,
            [NotNull] CachingLexerService cachingLexerService,
            [NotNull] ICommandProcessor commandProcessor,
            [NotNull] IPsiServices psiServices,
            [NotNull] IExternalIntellisenseHost externalIntellisenseHost,
            [NotNull] SkippingTypingAssist skippingTypingAssist,
            [NotNull] LastTypingAction lastTypingAction,
            [NotNull] ITypingAssistManager manager,
            [NotNull] StructuralRemoveManager structuralRemoveManager)
            : base(solution, settingsStore, cachingLexerService, commandProcessor, psiServices, externalIntellisenseHost, skippingTypingAssist, lastTypingAction, structuralRemoveManager)
        {
            manager.AddActionHandler(lifetime, TextControlActions.ActionIds.Enter, this, HandleEnter, IsActionHandlerAvailable);
        }

        protected override bool IsSupported(ITextControl textControl) => true;
        public bool QuickCheckAvailability(ITextControl textControl, IPsiSourceFile psiSourceFile) => psiSourceFile.LanguageType.Is<GherkinProjectFileType>();

        private bool HandleEnter([NotNull] IActionContext context)
        {
            if (context.EnsureWritable() != EnsureWritableResult.SUCCESS)
                return false;

            var textControl = context.TextControl;
            var cachingLexer = GetCachingLexer(textControl);
            if (cachingLexer == null)
                return false;

            if (GetTypingAssistOption(textControl, TypingAssistOptions.SmartIndentOnEnterExpression))
            {
                using (CommandProcessor.UsingCommand("Smart Enter"))
                {
                    if (textControl.Selection.OneDocRangeWithCaret().Length > 0)
                        return false;

                    var caret = textControl.Caret.Offset();
                    if (caret == 0)
                        return false;

                    var lastKeywordToken = FindLastKeywordToken(cachingLexer, caret);
                    if (lastKeywordToken == null)
                        return false;

                    var extraIdentSize = 0;
                    if (lastKeywordToken == GherkinTokenTypes.FEATURE_KEYWORD)
                        extraIdentSize = GetFormatSettingsKey(textControl).ScenarioIndentSize;
                    else if (lastKeywordToken == GherkinTokenTypes.RULE_KEYWORD)
                        extraIdentSize = GetFormatSettingsKey(textControl).ScenarioIndentSize;
                    else if (lastKeywordToken == GherkinTokenTypes.EXAMPLE_KEYWORD)
                        extraIdentSize = GetFormatSettingsKey(textControl).TableIndentSize;
                    else if (lastKeywordToken == GherkinTokenTypes.BACKGROUND_KEYWORD)
                        extraIdentSize = GetFormatSettingsKey(textControl).StepIndentSize;
                    else if (lastKeywordToken == GherkinTokenTypes.SCENARIO_KEYWORD)
                        extraIdentSize = GetFormatSettingsKey(textControl).StepIndentSize;
                    else if (lastKeywordToken == GherkinTokenTypes.SCENARIO_OUTLINE_KEYWORD)
                        extraIdentSize = GetFormatSettingsKey(textControl).StepIndentSize;
                    else if (lastKeywordToken == GherkinTokenTypes.EXAMPLES_KEYWORD)
                        extraIdentSize = GetFormatSettingsKey(textControl).TableIndentSize;
                    else if (lastKeywordToken == GherkinTokenTypes.EXAMPLES_KEYWORD)
                        extraIdentSize = GetFormatSettingsKey(textControl).StepIndentSize;
                    else if (lastKeywordToken == GherkinTokenTypes.STEP_KEYWORD)
                        extraIdentSize = 0;

                    var currentIndent = ComputeIndentOfCurrentKeyword(cachingLexer) + GetIndentText(textControl, extraIdentSize);

                    textControl.Document.InsertText(caret, GetNewLineText(textControl) + currentIndent);
                    return true;
                }
            }

            return false;
        }

        private string GetNewLineText(ITextControl textControl)
        {
            return GetNewLineText(textControl.Document.GetPsiSourceFile(Solution));
        }

        private string GetIndentText(ITextControl textControl, int indentSize)
        {
            if (indentSize == 0)
                return string.Empty;

            var sb = new StringBuilder();
            switch (GetIndentType(textControl))
            {
                case IndentStyle.Tab:
                    for (var i = 0; i < indentSize; i++)
                        sb.Append("\t");
                    break;
                case IndentStyle.Space:
                    var configIndentSize = GetIndentSize(textControl);
                    for (var i = 0; i < indentSize; i++)
                        for (int j = 0; j < configIndentSize; j++)
                            sb.Append(" ");
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
            return sb.ToString();
        }

        private string ComputeIndentOfCurrentKeyword(CachingLexer cachingLexer)
        {
            if (cachingLexer.CurrentPosition == 0)
                return string.Empty;
            cachingLexer.SetCurrentToken(cachingLexer.CurrentPosition - 1);
            if (cachingLexer.TokenType == GherkinTokenTypes.WHITE_SPACE)
                return cachingLexer.GetTokenText();
            return string.Empty;
        }

        private TokenNodeType FindLastKeywordToken(CachingLexer cachingLexer, int caret)
        {
            cachingLexer.FindTokenAt(caret - 1);
            while (!GherkinTokenTypes.KEYWORDS[cachingLexer.TokenType])
            {
                if (cachingLexer.CurrentPosition == 0)
                    return null;
                cachingLexer.SetCurrentToken(cachingLexer.CurrentPosition - 1);
            }
            return cachingLexer.TokenType;
        }


        private IndentStyle GetIndentType(ITextControl textControl)
        {
            return GetFormatSettingsKey(textControl).INDENT_STYLE;
        }

        private int GetIndentSize(ITextControl textControl)
        {
            return GetFormatSettingsKey(textControl).INDENT_SIZE;
        }

        private GherkinFormatSettingsKey GetFormatSettingsKey(ITextControl textControl)
        {
            var document = textControl.Document;
            var sourceFile = document.GetPsiSourceFile(Solution).NotNull("psiSourceFile is null for {0}", document);
            var formatSettingsKeyBase = sourceFile.GetFormatterSettings(sourceFile.PrimaryPsiLanguage);
            return (GherkinFormatSettingsKey) formatSettingsKeyBase;
        }
    }
}