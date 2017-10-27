// Python Tools for Visual Studio
// Copyright(c) Microsoft Corporation
// All rights reserved.
//
// Licensed under the Apache License, Version 2.0 (the License); you may not use
// this file except in compliance with the License. You may obtain a copy of the
// License at http://www.apache.org/licenses/LICENSE-2.0
//
// THIS CODE IS PROVIDED ON AN  *AS IS* BASIS, WITHOUT WARRANTIES OR CONDITIONS
// OF ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING WITHOUT LIMITATION ANY
// IMPLIED WARRANTIES OR CONDITIONS OF TITLE, FITNESS FOR A PARTICULAR PURPOSE,
// MERCHANTABLITY OR NON-INFRINGEMENT.
//
// See the Apache Version 2.0 License for specific language governing
// permissions and limitations under the License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.PythonTools.Editor;
using Microsoft.PythonTools.Editor.Core;
using Microsoft.PythonTools.Infrastructure;
using Microsoft.PythonTools.Parsing;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Editor.OptionsExtensionMethods;
using Microsoft.VisualStudio.TextManager.Interop;

namespace Microsoft.PythonTools.Intellisense {
    class ExpansionClient : IVsExpansionClient {
        private readonly PythonEditorServices _services;
        private readonly IVsTextLines _lines;
        private readonly IVsExpansion _expansion;
        private readonly IVsTextView _view;
        private readonly ITextView _textView;
        private IVsExpansionSession _session;
        private bool _sessionEnded, _selectEndSpan;
        private ITrackingPoint _selectionStart, _selectionEnd;

        public const string SurroundsWith = "SurroundsWith";
        public const string SurroundsWithStatement = "SurroundsWithStatement";
        public const string Expansion = "Expansion";

        public ExpansionClient(ITextView textView, PythonEditorServices services) {
            _textView = textView;
            _services = services;
            _view = _services.EditorAdaptersFactoryService.GetViewAdapter(_textView);
            _lines = (IVsTextLines)_services.EditorAdaptersFactoryService.GetBufferAdapter(_textView.TextBuffer);
            _expansion = _lines as IVsExpansion;
            if (_expansion == null) {
                throw new ArgumentException("TextBuffer does not support expansions");
            }
        }

        public bool InSession {
            get {
                return _session != null;
            }
        }

        public int EndExpansion() {
            _session = null;
            _sessionEnded = true;
            _selectionStart = _selectionEnd = null;
            return VSConstants.S_OK;
        }

        private static int TryGetXmlNodes(IVsExpansionSession session, out XElement header, out XElement snippet) {
            MSXML.IXMLDOMNode headerNode, snippetNode;
            int hr;
            header = null;
            snippet = null;

            if (ErrorHandler.Failed(hr = session.GetHeaderNode(null, out headerNode))) {
                return hr;
            }

            if (ErrorHandler.Failed(hr = session.GetSnippetNode(null, out snippetNode))) {
                return hr;
            }

            header = XElement.Parse(headerNode.xml);
            snippet = XElement.Parse(snippetNode.xml);

            return VSConstants.S_OK;
        }

        public int FormatSpan(IVsTextLines pBuffer, TextSpan[] ts) {
            XElement header, snippet;

            int hr = TryGetXmlNodes(_session, out header, out snippet);
            if (ErrorHandler.Failed(hr)) {
                return hr;
            }

            var ns = header.Name.NamespaceName;
            bool surroundsWith = header
                .Elements(XName.Get("SnippetTypes", ns))
                .Elements(XName.Get("SnippetType", ns))
                .Any(n => n.Value == SurroundsWith);

            bool surroundsWithStatement = header
                .Elements(XName.Get("SnippetTypes", ns))
                .Elements(XName.Get("SnippetType", ns))
                .Any(n => n.Value == SurroundsWithStatement);

            ns = snippet.Name.NamespaceName;
            var declList = snippet
                .Element(XName.Get("Declarations", ns))?
                .Elements()
                .Elements(XName.Get("ID", ns))
                .Select(n => n.Value)
                .Where(n => !string.IsNullOrEmpty(n))
                .ToList() ?? new List<string>();

            var importList = snippet
                .Element(XName.Get("Imports", ns))?
                .Elements(XName.Get("Import", ns))
                .Elements(XName.Get("Namespace", ns))
                .Select(n => n.Value)
                .Where(n => !string.IsNullOrEmpty(n))
                .ToList() ?? new List<string>();

            var codeText = snippet
                .Element(XName.Get("Code", ns))?
                .Value ?? string.Empty;

            // get the indentation of where we're inserting the code...
            string baseIndentation = GetBaseIndentation(ts);
            int startPosition;
            pBuffer.GetPositionOfLineIndex(ts[0].iStartLine, ts[0].iStartIndex, out startPosition);
            var insertTrackingPoint = _textView.TextBuffer.CurrentSnapshot.CreateTrackingPoint(startPosition, PointTrackingMode.Positive);

            TextSpan? endSpan = null;
            using (var edit = _textView.TextBuffer.CreateEdit()) {
                if (surroundsWith || surroundsWithStatement) {
                    // this is super annoyning...  Most languages can do a surround with and $selected$ can be
                    // an empty string and everything's the same.  But in Python we can't just have something like
                    // "while True: " without a pass statement.  So if we start off with an empty selection we
                    // need to insert a pass statement.  This is the purpose of the "SurroundsWithStatement"
                    // snippet type.
                    //
                    // But, to make things even more complicated, we don't have a good indication of what's the 
                    // template text vs. what's the selected text.  We do have access to the original template,
                    // but all of the values have been replaced with their default values when we get called
                    // here.  So we need to go back and re-apply the template, except for the $selected$ part.
                    //
                    // Also, the text has \n, but the inserted text has been replaced with the appropriate newline
                    // character for the buffer.
                    var templateText = codeText.Replace("\n", _textView.Options.GetNewLineCharacter());
                    foreach (var decl in declList) {
                        string defaultValue;
                        if (ErrorHandler.Succeeded(_session.GetFieldValue(decl, out defaultValue))) {
                            templateText = templateText.Replace("$" + decl + "$", defaultValue);
                        }
                    }
                    templateText = templateText.Replace("$end$", "");

                    // we can finally figure out where the selected text began witin the original template...
                    int selectedIndex = templateText.IndexOf("$selected$");
                    if (selectedIndex != -1) {
                        var selection = _textView.Selection;
                        
                        // now we need to get the indentation of the $selected$ element within the template,
                        // as we'll need to indent the selected code to that level.
                        string indentation = GetTemplateSelectionIndentation(templateText, selectedIndex);

                        var start = _selectionStart.GetPosition(_textView.TextBuffer.CurrentSnapshot);
                        var end = _selectionEnd.GetPosition(_textView.TextBuffer.CurrentSnapshot);
                        if (end < start) {
                            // we didn't actually have a selction, and our negative tracking pushed us
                            // back to the start of the buffer...
                            end = start;
                        }
                        var selectedSpan = Span.FromBounds(start, end);

                        if (surroundsWithStatement && 
                            String.IsNullOrWhiteSpace(_textView.TextBuffer.CurrentSnapshot.GetText(selectedSpan))) {
                            // we require a statement here and the user hasn't selected any code to surround,
                            // so we insert a pass statement (and we'll select it after the completion is done)
                            edit.Replace(new Span(startPosition + selectedIndex, end - start), "pass");

                            // Surround With can be invoked with no selection, but on a line with some text.
                            // In that case we need to inject an extra new line.
                            var endLine = _textView.TextBuffer.CurrentSnapshot.GetLineFromPosition(end);
                            var endText = endLine.GetText().Substring(end - endLine.Start);
                            if (!String.IsNullOrWhiteSpace(endText)) {
                                edit.Insert(end, _textView.Options.GetNewLineCharacter());
                            }

                            // we want to leave the pass statement selected so the user can just
                            // continue typing over it...
                            var startLine = _textView.TextBuffer.CurrentSnapshot.GetLineFromPosition(startPosition + selectedIndex);                            
                            _selectEndSpan = true;
                            endSpan = new TextSpan() {
                                iStartLine = startLine.LineNumber,
                                iEndLine = startLine.LineNumber,
                                iStartIndex = baseIndentation.Length + indentation.Length,
                                iEndIndex = baseIndentation.Length + indentation.Length + 4,
                            };
                        }

                        IndentSpan(
                            edit, 
                            indentation,
                            _textView.TextBuffer.CurrentSnapshot.GetLineFromPosition(start).LineNumber + 1, // 1st line is already indented
                            _textView.TextBuffer.CurrentSnapshot.GetLineFromPosition(end).LineNumber
                        );
                    }
                }

                // we now need to update any code which was not selected  that we just inserted.
                IndentSpan(edit, baseIndentation, ts[0].iStartLine + 1, ts[0].iEndLine);

                edit.Apply();
            }

            if (endSpan != null) {
                _session.SetEndSpan(endSpan.Value);
            }

            // add any missing imports...
            AddMissingImports(
                importList,
                insertTrackingPoint.GetPoint(_textView.TextBuffer.CurrentSnapshot)
            );

            return hr;
        }

        private void AddMissingImports(List<string> importList, SnapshotPoint point) {
            if (importList.Count == 0) {
                return;
            }

            var bi = _services.GetBufferInfo(_textView.TextBuffer);
            var entry = bi?.AnalysisEntry;
            if (entry == null) {
                return;
            }

            SourceLocation loc;
            try {
                loc = point.ToSourceLocation();
            } catch (ArgumentException ex) {
                Debug.Fail(ex.ToUnhandledExceptionMessage(GetType()));
                return;
            }

            foreach (var import in importList) {
                var isMissing = entry.Analyzer.WaitForRequest(
                    entry.Analyzer.IsMissingImportAsync(entry, import, loc),
                    "ExpansionClient.IsMissingImportAsync",
                    false
                );

                if (isMissing) {
                    VsProjectAnalyzer.AddImport(_textView.TextBuffer, null, import);
                }
            }
        }

        private static string GetTemplateSelectionIndentation(string templateText, int selectedIndex) {
            string indentation = "";
            for (int i = selectedIndex - 1; i >= 0; i--) {
                if (templateText[i] != '\t' && templateText[i] != ' ') {
                    indentation = templateText.Substring(i + 1, selectedIndex - i - 1);
                    break;
                }
            }
            return indentation;
        }

        private string GetBaseIndentation(TextSpan[] ts) {
            var indentationLine = _textView.TextBuffer.CurrentSnapshot.GetLineFromLineNumber(ts[0].iStartLine).GetText();
            string baseIndentation = indentationLine;
            for (int i = 0; i < indentationLine.Length; i++) {
                if (indentationLine[i] != ' ' && indentationLine[i] != '\t') {
                    baseIndentation = indentationLine.Substring(0, i);
                    break;
                }
            }
            return baseIndentation;
        }

        private void IndentSpan(ITextEdit edit, string indentation, int startLine, int endLine) {
            var snapshot = _textView.TextBuffer.CurrentSnapshot;
            for (int i = startLine; i <= endLine; i++) {
                var curline = snapshot.GetLineFromLineNumber(i);
                edit.Insert(curline.Start, indentation);
            }
        }

        public int GetExpansionFunction(MSXML.IXMLDOMNode xmlFunctionNode, string bstrFieldName, out IVsExpansionFunction pFunc) {
            pFunc = null;
            return VSConstants.S_OK;
        }

        public int IsValidKind(IVsTextLines pBuffer, TextSpan[] ts, string bstrKind, out int pfIsValidKind) {
            pfIsValidKind = 1;
            return VSConstants.S_OK;
        }

        public int IsValidType(IVsTextLines pBuffer, TextSpan[] ts, string[] rgTypes, int iCountTypes, out int pfIsValidType) {
            pfIsValidType = 1;
            return VSConstants.S_OK;
        }

        public int OnAfterInsertion(IVsExpansionSession pSession) {
            return VSConstants.S_OK;
        }

        public int OnBeforeInsertion(IVsExpansionSession pSession) {
            _session = pSession;
            return VSConstants.S_OK;
        }

        public int OnItemChosen(string pszTitle, string pszPath) {
            int caretLine, caretColumn;
            GetCaretPosition(out caretLine, out caretColumn);

            var textSpan = new TextSpan() { iStartLine = caretLine, iStartIndex = caretColumn, iEndLine = caretLine, iEndIndex = caretColumn };
            return InsertNamedExpansion(pszTitle, pszPath, textSpan);
        }

        public int InsertNamedExpansion(string pszTitle, string pszPath, TextSpan textSpan) {
            if (_session != null) {
                // if the user starts an expansion session while one is in progress
                // then abort the current expansion session
                _session.EndCurrentExpansion(1);
                _session = null;
            }

            var selection = _textView.Selection;
            var snapshot = selection.Start.Position.Snapshot;

            _selectionStart = snapshot.CreateTrackingPoint(selection.Start.Position, VisualStudio.Text.PointTrackingMode.Positive);
            _selectionEnd = snapshot.CreateTrackingPoint(selection.End.Position, VisualStudio.Text.PointTrackingMode.Negative);
            _selectEndSpan = _sessionEnded = false;

            int hr = _expansion.InsertNamedExpansion(
                pszTitle,
                pszPath,
                textSpan,
                this,
                GuidList.guidPythonLanguageServiceGuid,
                0,
                out _session
            );

            if (ErrorHandler.Succeeded(hr)) {
                if (_sessionEnded) {
                    _session = null;
                }
            }
            return hr;
        }

        public int NextField() {
            return _session.GoToNextExpansionField(0);
        }

        public int PreviousField() {
            return _session.GoToPreviousExpansionField();
        }

        public int EndCurrentExpansion(bool leaveCaret) {
            if (_selectEndSpan) {
                TextSpan[] endSpan = new TextSpan[1];
                if (ErrorHandler.Succeeded(_session.GetEndSpan(endSpan))) {
                    var snapshot = _textView.TextBuffer.CurrentSnapshot;
                    var startLine = snapshot.GetLineFromLineNumber(endSpan[0].iStartLine);
                    var span = new Span(startLine.Start + endSpan[0].iStartIndex, 4);
                    _textView.Caret.MoveTo(new SnapshotPoint(snapshot, span.Start));
                    _textView.Selection.Select(new SnapshotSpan(_textView.TextBuffer.CurrentSnapshot, span), false);
                    return _session.EndCurrentExpansion(1);
                }
            }
            return _session.EndCurrentExpansion(leaveCaret ? 1 : 0);
        }

        public int PositionCaretForEditing(IVsTextLines pBuffer, TextSpan[] ts) {
            return VSConstants.S_OK;
        }

        private void GetCaretPosition(out int caretLine, out int caretColumn) {
            ErrorHandler.ThrowOnFailure(_view.GetCaretPos(out caretLine, out caretColumn));

            // Handle virtual space
            int lineLength;
            ErrorHandler.ThrowOnFailure(_lines.GetLengthOfLine(caretLine, out lineLength));

            if (caretColumn > lineLength) {
                caretColumn = lineLength;
            }
        }
    }
}
