﻿// Copyright (c) Microsoft Corporation.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

namespace Microsoft.VisualStudio.FSharp.Editor

open System
open System.Composition
open System.Collections.Concurrent
open System.Collections.Generic
open System.Collections.Immutable
open System.Threading
open System.Threading.Tasks
open System.Linq
open System.Runtime.CompilerServices
open System.Windows
open System.Windows.Controls
open System.Windows.Media

open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.Completion
open Microsoft.CodeAnalysis.Classification
open Microsoft.CodeAnalysis.Editor
open Microsoft.CodeAnalysis.Editor.Implementation.IntelliSense.QuickInfo
open Microsoft.CodeAnalysis.Editor.Shared.Utilities
open Microsoft.CodeAnalysis.Formatting
open Microsoft.CodeAnalysis.Host.Mef
open Microsoft.CodeAnalysis.Options
open Microsoft.CodeAnalysis.Text

open Microsoft.VisualStudio.FSharp.LanguageService
open Microsoft.VisualStudio.Text
open Microsoft.VisualStudio.Text.Classification
open Microsoft.VisualStudio.Text.Tagging
open Microsoft.VisualStudio.Text.Formatting
open Microsoft.VisualStudio.Shell
open Microsoft.VisualStudio.Shell.Interop

open Microsoft.FSharp.Compiler
open Microsoft.FSharp.Compiler.Parser
open Microsoft.FSharp.Compiler.Range
open Microsoft.FSharp.Compiler.SourceCodeServices
open System.Windows.Documents

// FSROSLYNTODO: with the merge of the below PR, the QuickInfo API should be changed
// to allow for a more flexible syntax for defining the content of the tooltip.
// The below interface should be discarded then or updated accourdingly.
// https://github.com/dotnet/roslyn/pull/13623
type internal FSharpDeferredQuickInfoContent(content: string, textProperties: TextFormattingRunProperties) =
    interface IDeferredQuickInfoContent with
        override this.Create() : FrameworkElement =
            let textBlock = TextBlock(Run(content), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None)
            textBlock.SetValue(TextElement.BackgroundProperty, textProperties.BackgroundBrush)
            textBlock.SetValue(TextElement.ForegroundProperty, textProperties.ForegroundBrush)
            textBlock.SetValue(TextElement.FontFamilyProperty, textProperties.Typeface.FontFamily)
            textBlock.SetValue(TextElement.FontSizeProperty, textProperties.FontRenderingEmSize)
            textBlock.SetValue(TextElement.FontStyleProperty, if textProperties.Italic then FontStyles.Italic else FontStyles.Normal)
            textBlock.SetValue(TextElement.FontWeightProperty, if textProperties.Bold then FontWeights.Bold else FontWeights.Normal)
            upcast textBlock

[<Shared>]
[<ExportQuickInfoProvider(PredefinedQuickInfoProviderNames.Semantic, FSharpCommonConstants.FSharpLanguageName)>]
type internal FSharpQuickInfoProvider 
    [<System.ComponentModel.Composition.ImportingConstructor>] 
    (
        [<System.ComponentModel.Composition.Import(typeof<SVsServiceProvider>)>] serviceProvider: IServiceProvider,
        classificationFormatMapService: IClassificationFormatMapService,
        checkerProvider: FSharpCheckerProvider,
        projectInfoManager: ProjectInfoManager
    ) =

    let xmlMemberIndexService = serviceProvider.GetService(typeof<SVsXMLMemberIndexService>) :?> IVsXMLMemberIndexService
    let documentationBuilder = XmlDocumentation.CreateDocumentationBuilder(xmlMemberIndexService, serviceProvider.DTE)
    
    static member ProvideQuickInfo(checker: FSharpChecker, documentId: DocumentId, sourceText: SourceText, filePath: string, position: int, options: FSharpProjectOptions, textVersionHash: int) =
        async {
            let! _parseResults, checkResultsAnswer = checker.ParseAndCheckFileInProject(filePath, textVersionHash, sourceText.ToString(), options)
            let checkFileResults = 
                match checkResultsAnswer with
                | FSharpCheckFileAnswer.Aborted -> failwith "Compilation isn't complete yet"
                | FSharpCheckFileAnswer.Succeeded(results) -> results
          
            let textLine = sourceText.Lines.GetLineFromPosition(position)
            let textLineNumber = textLine.LineNumber + 1 // Roslyn line numbers are zero-based
            //let qualifyingNames, partialName = QuickParse.GetPartialLongNameEx(textLine.ToString(), textLineColumn - 1)
            let defines = CompilerEnvironment.GetCompilationDefinesForEditing(filePath, options.OtherOptions |> Seq.toList)
            
            match CommonHelpers.getSymbolAtPosition(documentId, sourceText, position, filePath, defines, SymbolLookupKind.Fuzzy) with 
            | Some symbol -> 
                let! res = checkFileResults.GetToolTipTextAlternate(textLineNumber, symbol.RightColumn, textLine.ToString(), [symbol.Text], FSharpTokenTag.IDENT)
                return 
                    match res with
                    | FSharpToolTipText [] 
                    | FSharpToolTipText [FSharpToolTipElement.None] -> None
                    | _ -> Some(res, CommonRoslynHelpers.FSharpRangeToTextSpan(sourceText, symbol.Range))
            | None -> return None
        }
    
    interface IQuickInfoProvider with
        override this.GetItemAsync(document: Document, position: int, cancellationToken: CancellationToken): Task<QuickInfoItem> =
            async {
                let! sourceText = document.GetTextAsync(cancellationToken)
                let defines = projectInfoManager.GetCompilationDefinesForEditingDocument(document)  

                match CommonHelpers.getSymbolAtPosition(document.Id, sourceText, position, document.FilePath, defines, SymbolLookupKind.Fuzzy) with
                | Some _ ->
                    match projectInfoManager.TryGetOptionsForEditingDocumentOrProject(document) with 
                    | Some options ->
                        let! textVersion = document.GetTextVersionAsync(cancellationToken)
                        let! quickInfoResult = FSharpQuickInfoProvider.ProvideQuickInfo(checkerProvider.Checker, document.Id, sourceText, document.FilePath, position, options, textVersion.GetHashCode())
                        match quickInfoResult with
                        | Some(toolTipElement, textSpan) ->
                            let dataTipText = XmlDocumentation.BuildDataTipText(documentationBuilder, toolTipElement)
                            let textProperties = classificationFormatMapService.GetClassificationFormatMap("tooltip").DefaultTextProperties
                            return QuickInfoItem(textSpan, FSharpDeferredQuickInfoContent(dataTipText, textProperties))
                        | None -> return null
                    | None -> return null
                | None -> return null
            } |> CommonRoslynHelpers.StartAsyncAsTask(cancellationToken)