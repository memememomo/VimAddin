//
// ViMode.cs
//
// Author:
//   Michael Hutchinson <mhutchinson@novell.com>
//
// Copyright (C) 2008 Novell, Inc (http://www.novell.com)
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//

using System;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Linq;
using Mono.TextEditor;
using MonoDevelop.Ide.CodeFormatting;
namespace VimAddin
{
	/**
	Represent a keystroke that produces input in the text document e.g. characters, newlines, tabs.
	*/
	public struct Keystroke
	{
		private Gdk.Key m_key;
		public Gdk.Key Key { get { return m_key; } }
		private uint m_unicodeKey; 
		public uint UnicodeKey { get { return m_unicodeKey; } }
		private Gdk.ModifierType m_modifier;
		public Gdk.ModifierType Modifier { get { return m_modifier; } }
		public Keystroke(Gdk.Key key, uint unicodeKey, Gdk.ModifierType modifier)
		{
			m_key = key;
			m_unicodeKey = unicodeKey;
			m_modifier = modifier;
		}
	}

	public class ViEditMode : Mono.TextEditor.EditMode
	{
		// State variable indicates that we have started recording actions into LastAction
		bool lastActionStarted;
		bool searchBackward;
		static string lastPattern;
		static string lastReplacement;
		State curState;
		protected State CurState {
			get {
				return curState;
			}
			set {
				curState = value;
				if (statusArea != null) {
					statusArea.ShowCaret = curState == State.Command;
				}
			}
		}
		protected State PrevState { get; set; }

		Motion motion;
		const string substMatch = @"^:s(?<sep>.)(?<pattern>.+?)\k<sep>(?<replacement>.*?)(\k<sep>(?<trailer>[gi]+?))?$";
		const string SpecialMarks = "`";
		StringBuilder commandBuffer = new StringBuilder ();
		protected Dictionary<char,ViMark> marks = new Dictionary<char, ViMark>();
		Dictionary<char,ViMacro> macros = new Dictionary<char, ViMacro>();
		char macros_lastplayed = '@'; // start with the illegal macro character
		string statusText = "";
		List<Action<TextEditorData>> LastAction = new List<Action<TextEditorData>>();
		// Lines, RightOffsetOnLastLine
		Tuple<int, int> LastSelectionRange = new Tuple<int,int>(0,0);

		/**
		Cache keystroke sequence from last insert operation.
		*/
		List<Keystroke> LastInsert = new List<Keystroke>();

		/**
		Cache keystrokes during insert operation that produce insert keys.
		*/
		List<Keystroke> InsertBuffer = new List<Keystroke>();

		/// <summary>
		/// Number of times to perform the next action
		/// For example 3 is the numeric prefix when "3w" is entered
		/// <summary>
		string numericPrefix = "";

		/// <summary>
		/// Number of times to perform the next action
		/// <summary>
		int repeatCount {
			get {
				int n;
				int.TryParse (numericPrefix, out n);
				return n < 1 ? 1 : n;
			}
			set {
				numericPrefix = value.ToString ();
			}
		}

		/// <summary>
		/// Whether ViEditMode is in a state where it should accept a numeric prefix
		/// <summary>
		bool AcceptNumericPrefix {
			get {
				return CurState == State.Normal || CurState == State.Delete || CurState == State.Change
				|| CurState == State.Yank || CurState == State.Indent || CurState == State.Unindent;
			}
		}
		/// The macro currently being implemented. Will be set to null and checked as a flag when required.
		/// </summary>
		ViMacro currentMacro;
		
		public virtual string Status {
		
			get {
				return statusArea.Message;
			}
			
			protected set {
				if (currentMacro != null) {
					value = value + " recording";
				}
				if (viTextEditor != null) {
					statusArea.Message = value;
				}
			}
		}

		protected MonoDevelop.Ide.Gui.Document saveableDocument;

		public ViEditMode( MonoDevelop.Ide.Gui.Document doc )
		{
			saveableDocument = doc;
		}

		public bool HasData( )
		{
			return Data != null;
		}

		public bool HasTextEditorData( )
		{
			return textEditorData != null;
		}

		protected void FormatSelection( )
		{
			var doc = saveableDocument;
			var formatter = CodeFormatterService.GetFormatter (Document.MimeType);
			if (formatter == null)
				return;

			TextSegment selection;
			var editor = Data;
			if (editor.IsSomethingSelected) {
				selection = editor.SelectionRange;
			} else {
				selection = editor.GetLine (editor.Caret.Line).Segment;
			}

			using (var undo = editor.OpenUndoGroup ()) {
				var version = editor.Version;

				if (formatter.SupportsOnTheFlyFormatting) {
					formatter.OnTheFlyFormat (doc, selection.Offset, selection.EndOffset);
				} else {
					var pol = doc.Project != null ? doc.Project.Policies : null;
					try {
						string text = formatter.FormatText (pol, editor.Text, selection.Offset, selection.EndOffset);
						if (text != null) {
							editor.Replace (selection.Offset, selection.Length, text);
						}
					} catch (Exception e) {
						Console.WriteLine ("Error during format.");
					}
				}

				if (editor.IsSomethingSelected) {
					int newOffset = version.MoveOffsetTo (editor.Version, selection.Offset);
					int newEndOffset = version.MoveOffsetTo (editor.Version, selection.EndOffset);
					editor.SetSelection (newOffset, newEndOffset);
				}
			}
		}

		protected virtual string RunExCommand (string command)
		{
			// by default, return to normal state after executing a command
			CurState = State.Normal;

			switch (command [0]) {
			case ':':
				if (2 > command.Length)
					break;
					
				int line;
				if (int.TryParse (command.Substring (1), out line)) {
					if (line < DocumentLocation.MinLine) {
						line = DocumentLocation.MinLine;
					} else if (line > Data.Document.LineCount) {
						line = Data.Document.LineCount;
					} else if (line == 0) {
						RunAction (CaretMoveActions.ToDocumentStart);
						return "Jumped to beginning of document.";
					}
					
					Data.Caret.Line = line;
					Editor.ScrollToCaret ();
					return string.Format ("Jumped to line {0}.", line);
				}
	
				switch (command [1]) {
				case 's':
					if (2 == command.Length) {
						if (null == lastPattern || null == lastReplacement)
							return "No stored pattern.";
							
						// Perform replacement with stored stuff
						command = string.Format (":s/{0}/{1}/", lastPattern, lastReplacement);
					}
		
					var match = Regex.Match (command, substMatch, RegexOptions.Compiled);
					if (!(match.Success && match.Groups ["pattern"].Success && match.Groups ["replacement"].Success))
						break;
		
					return RegexReplace (match);
					
				case '$':
					if (command.Length == 2) {
						//RunAction (CaretMoveActions.ToDocumentEnd);
						RunAction (ToDocumentEnd);
						return "Jumped to end of document.";
					}
					break;	
				}
				if (command.Substring (1) == "test") {
					CurState = State.Confirm;
					var selection = Data.SelectionRange;
					int lineStart, lineEnd;
					int lookaheadOffset = Math.Min (Caret.Offset + 1000, Data.Document.TextLength);
					MiscActions.GetSelectedLines (Data, out lineStart, out lineEnd);
					int roffset = Data.SelectionRange.Offset;
					int linesAhead = Math.Min(LastSelectionRange.Item1, Document.LineCount - Caret.Line);
//					CaretMoveActions.LineStart(Data);
					SelectionActions.StartSelection (Data);
					if (linesAhead > 1) {
						for (int i = 0; i < linesAhead; ++i) {
							ViActions.Down (Data);
						}
						CaretMoveActions.LineStart (Data);
					}

					DocumentLine lastLine = Document.GetLineByOffset (Caret.Offset);
					int lastLineOffset = Math.Min (LastSelectionRange.Item2, lastLine.EndOffset);
					for (int i = 0; i < lastLineOffset; ++i) {
						ViActions.Right (Data);
					}
					SelectionActions.EndSelection (Data);
//					SelectionActions.StartSelection (Data);
//					CaretMoveActions.LineFirstNonWhitespace (Data);
//					ViActions.Down (Data);
//					ViActions.Right (Data);
//					ViActions.Right (Data);
//					ViActions.Right (Data);
//					ViActions.Right (Data);
//					ViActions.Right (Data);
//					SelectionActions.EndSelection (Data);

					//return string.Format("line range: {0} {1}", lineStart, lineEnd);
					return string.Format("s:{0} {1}; ({2},{3}) ({4}) ({5})",
						selection.Offset,
						selection.Length,
						LastSelectionRange.Item1,
						LastSelectionRange.Item2,
						linesAhead,
						Data.Document.TextLength);
				}
				break;
			// case ':'
				
			case '?':
			case '/':
				searchBackward = ('?' == command[0]);
				if (1 < command.Length) {
					Editor.HighlightSearchPattern = true;
					Editor.SearchEngine = new RegexSearchEngine ();
					var pattern = command.Substring (1);
					Editor.SearchPattern = pattern;
					var caseSensitive = pattern.ToCharArray ().Any (c => char.IsUpper (c));
					Editor.SearchEngine.SearchRequest.CaseSensitive = caseSensitive;
				}
				return Search ();
			}

			return "Command not recognised";
		}
		
		void SearchWordAtCaret (bool backward)
		{
			Editor.SearchEngine = new RegexSearchEngine ();
			var s = Data.FindCurrentWordStart (Data.Caret.Offset);
			var e = Data.FindCurrentWordEnd (Data.Caret.Offset);
			if (s < 0 || e <= s)
				return;
			
			var word = Document.GetTextBetween (s, e);
			//use negative lookahead and lookbehind for word characters to make sure we only get fully matching words
			word = "(?<!\\w)" + System.Text.RegularExpressions.Regex.Escape (word) + "(?!\\w)";
			Editor.SearchPattern = word;
			Editor.SearchEngine.SearchRequest.CaseSensitive = true;
			searchBackward = backward;
			Search ();
		}

		void RepeatLastAction()
		{
			foreach (Action<TextEditorData> action in LastAction) {
				RunAction (action);
			}
		}

		/**
		Indent specified number of lines from the current caret position.
		*/
		protected Action<TextEditorData> MakeIndentAction(int lines)
		{
			var linesToIndent = lines;
			Action<TextEditorData> indentAction = delegate(TextEditorData data) {
				// programmatically generate a text selection
				List<Action<TextEditorData>> actions = new List<Action<TextEditorData>> ();
				RunAction (CaretMoveActions.LineFirstNonWhitespace);
				int roffset = Data.SelectionRange.Offset;
				actions.Add (SelectionActions.FromMoveAction (CaretMoveActions.LineEnd));
				for (int i = 1; i < linesToIndent; i++) {
					actions.Add (SelectionActions.FromMoveAction (ViActions.Down));
				}
				// then indent it
				actions.Add (MiscActions.IndentSelection);
				RunActions (actions.ToArray ());
				//set cursor to start of first line indented
				Caret.Offset = roffset;
				RunAction (CaretMoveActions.LineFirstNonWhitespace);

				Reset ("");
			};
			return indentAction;
		}

		/**
		Apply an action to a selection of the same size we selected previously.
		*/
		protected Action<TextEditorData> MakeActionToApplyToLastSelection(Action<TextEditorData> actionToApply)
		{
			// LastSelectionRange is set by SaveSelectionRange
			var lastLinesAhead = LastSelectionRange.Item1;
			var lastLastLineOffset = LastSelectionRange.Item2;
			Action<TextEditorData> action = delegate(TextEditorData data) {
				// generate a selection from last selection range
				SelectionActions.StartSelection(data);
				int linesAhead = Math.Min(lastLinesAhead, data.Document.LineCount - data.Caret.Line);
				SelectionActions.StartSelection (data);
				if (linesAhead > 1) {
					for (int i = 0; i < linesAhead; ++i) {
						ViActions.Down (data);
					}
					CaretMoveActions.LineStart (data);
				}
				DocumentLine lastLine = Document.GetLineByOffset (data.Caret.Offset);
				int lastLineOffset = Math.Min (lastLastLineOffset, lastLine.EndOffset);
				for (int i = 0; i < lastLineOffset; ++i) {
					ViActions.Right (data);
				}
				SelectionActions.EndSelection (data);

				// apply the action
				RunAction(actionToApply);
			};
			return action;
		}

		/**
		Make an action that does action1 and then action2.
		*/
		protected Action<TextEditorData> MakeActionPair(Action<TextEditorData> action1,
			Action<TextEditorData> action2)
		{
			Action<TextEditorData> action = delegate(TextEditorData data) {
				RunAction(action1);
				RunAction(action2);
			};
			return action;
		}

		/**
		An action that saves the current caret position in the special mark `.
		*/
		protected void SaveContextMark(TextEditorData data)
		{
			RunAction (marks ['`'].SaveMark);
		}

		protected void SaveSelectionRange(TextEditorData data)
		{
			int lineStart, lineEnd;
			MiscActions.GetSelectedLines (data, out lineStart, out lineEnd);
			int lines = lineEnd - lineStart + 1;
			int offset = 0;
			if (lines > 1) {
				var lastLine = data.Document.GetLineByOffset (data.SelectionRange.EndOffset);
				offset = data.SelectionRange.EndOffset - lastLine.Offset;
			} else {
				offset = data.SelectionRange.Length;
			}
			LastSelectionRange = new Tuple<int, int> (lines, offset);
		}

		/**
		Wrapper for ViActionMaps.GetNavCharAction. We catch and modify actions as necessary.
		*/
		protected Action<TextEditorData> GetNavCharAction(char unicodeKey, bool isCommand)
		{
			Action<TextEditorData> action = ViActionMaps.GetNavCharAction (unicodeKey, isCommand);
			if (action == MiscActions.GotoMatchingBracket) {
				action = MakeActionPair (SaveContextMark, action);
			}
			return action;
		}

		/**
		Unindent specified number of lines from the current caret position.
		*/
		public Action<TextEditorData> MakeUnindentAction(int lines)
		{
			var linesToUnindent = lines;
			Action<TextEditorData> unindentAction = delegate(TextEditorData data) {
				List<Action<TextEditorData>> actions = new List<Action<TextEditorData>> ();
				RunAction (CaretMoveActions.LineFirstNonWhitespace);
				int roffset = Data.SelectionRange.Offset; //save caret position
				actions.Add (SelectionActions.FromMoveAction (CaretMoveActions.LineEnd));
				for (int i = 1; i < linesToUnindent; i++) {
					actions.Add (SelectionActions.FromMoveAction (ViActions.Down));
				}
				actions.Add (MiscActions.RemoveIndentSelection);
				RunActions (actions.ToArray ());
				//set cursor to start of first line indented
				Caret.Offset = roffset;
				RunAction (CaretMoveActions.LineFirstNonWhitespace);

				Reset ("");
			};
			return unindentAction;
		}

		/**
		Action to go to the document end. This replaces CaretMoveActions.DocumentEnd, which puts us in a dicey spot.
		*/
		protected void ToDocumentEnd(TextEditorData data)
		{

			if (data.Document.LineCount > 1) {
				data.Caret.Line = data.Document.LineCount - 1;
			}
		}
		
		public override bool WantsToPreemptIM {
			get {
				return CurState != State.Insert && CurState != State.Replace;
			}
		}
		
		protected override void SelectionChanged ()
		{
			if (Data.IsSomethingSelected) {
				CurState = ViEditMode.State.Visual;
				Status = "-- VISUAL --";
			} else if (CurState == State.Visual && !Data.IsSomethingSelected) {
				Reset ("");
			}
		}
		
		protected override void CaretPositionChanged ()
		{
			if (CurState == State.Replace || CurState == State.Insert || CurState == State.Visual)
				return;
			else if (CurState == ViEditMode.State.Normal || CurState == ViEditMode.State.Unknown)
				ViActions.RetreatFromLineEnd (Data);
			else
				Reset ("");
		}

		ViStatusArea statusArea;
		TextEditor viTextEditor;

		void CheckVisualMode ()
		{
			if (Data == null) {
				return;
			}
			if (CurState == ViEditMode.State.Visual || CurState == ViEditMode.State.Visual) {
				if (!Data.IsSomethingSelected)
					CurState = ViEditMode.State.Normal;
			} else {
				if (Data.IsSomethingSelected) {
					CurState = ViEditMode.State.Visual;
					Status = "-- VISUAL --";
				}
			}
		}
		
		void ResetEditorState (TextEditorData data)
		{
			if (data == null)
				return;
			data.ClearSelection ();
			
			//Editor can be null during GUI-less tests
			// Commenting this fixes bug: Bug 622618 - Inline search fails in vi mode
//			if (Editor != null)
//				Editor.HighlightSearchPattern = false;
			
			if (CaretMode.Block != data.Caret.Mode) {
				data.Caret.Mode = CaretMode.Block;
				if (data.Caret.Column > DocumentLocation.MinColumn)
					data.Caret.Column--;
			}
			ViActions.RetreatFromLineEnd (data);
		}
		
		protected override void OnAddedToEditor (TextEditorData data)
		{
			data.Caret.Mode = CaretMode.Block;
			ViActions.RetreatFromLineEnd (data);

			viTextEditor = data.Parent;
			if (viTextEditor != null) {
				statusArea = new ViStatusArea (viTextEditor);
			}
		}
		
		protected override void OnRemovedFromEditor (TextEditorData data)
		{
			data.Caret.Mode = CaretMode.Insert;

			if (viTextEditor != null) {
				statusArea.RemoveFromParentAndDestroy ();
				statusArea = null;
				viTextEditor = null;
			}
		}

		public override void AllocateTextArea (TextEditor textEditor, TextArea textArea, Gdk.Rectangle allocation)
		{
			statusArea.AllocateArea (textArea, allocation);
		}
		
		void Reset (string status)
		{
			lastActionStarted = false;
			PrevState = State.Normal;
			CurState = State.Normal;
			ResetEditorState (Data);
			
			commandBuffer.Length = 0;
			Status = status;

		    numericPrefix = "";
		}
		
		protected virtual Action<TextEditorData> GetInsertAction (Gdk.Key key, Gdk.ModifierType modifier)
		{
			return ViActionMaps.GetInsertKeyAction (key, modifier) ??
				ViActionMaps.GetDirectionKeyAction (key, modifier);
		}

		private void StartNewLastAction(Action<TextEditorData> action)
		{
			lastActionStarted = true;
			LastAction.Clear ();
			LastAction.Add (action);
		}

		/// Run an action multiple times if it was preceded by a numeric key
		/// Resets numeric prefixs
		/// <summary>
		private void RunRepeatableAction (Action<TextEditorData> action)
		{
//			if (Data != null) {
//				Console.WriteLine ("RunRepeatableAction entering with Data not null");
//			}
			int reps = repeatCount;   //how many times to repeat command
			for (int i = 0; i < reps; i++) {
				RunAction (action);
			}
//			if (Data == null) {
//				Console.WriteLine ("RunRepeatableAction leaving Data null");
//			}
			numericPrefix = "";
		}

		/// <summary>
		/// Run the first action multiple times if it was preceded by a numeric key
		/// Run the following actions once each
		/// <summary>
		private void RunRepeatableActionChain (params Action<TextEditorData>[] actions)
		{
			List<Action<TextEditorData>> actionList = new List<Action<TextEditorData>> ();
			int reps = repeatCount;   //how many times to repeat command
			for (int i = 0; i < reps; i++) {
				actionList.Add (actions [0]);
			}
			for (int i = 1; i < actions.Length; i++) {
				actionList.Add (actions [i]);
			}
			RunActions (actionList.ToArray ());
			numericPrefix = "";
		}

		/// <summary>
		/// Repeat entire set of actions based on preceding numeric key
		/// The first action indicates the movement that initiates the line action
		/// The second action indicates the action to be taken on the line
		/// The third action indicates the action to reset after completing the action on that line
		/// <summary>
		private List<Action<TextEditorData>> GenerateRepeatedActionList (
			params Action<TextEditorData>[] actions)
		{
			List<Action<TextEditorData>> actionList = new List<Action<TextEditorData>> ();

			int reps = repeatCount;   //how many times to repeat command

			for (int i = 0; i < reps; i++) {
				actionList.AddRange (actions);
			}
			numericPrefix = "";
			return actionList;
		}

		private void RepeatLastInsert(TextEditorData data)
		{
			CurState = State.Insert;
			Caret.Mode = CaretMode.Insert;
			foreach (Keystroke ks in LastInsert) {
				// TODO: Handle full keystroke
				Action<TextEditorData> action = ViActionMaps.GetInsertKeyAction (ks.Key, ks.Modifier);
				if (action != null) {
					RunAction (action);
				} else {
					InsertCharacter (ks.UnicodeKey);
				}
			}
			Caret.Mode = CaretMode.Block;
			Reset ("");
		}

		protected override void HandleKeypress (Gdk.Key key, uint unicodeKey, Gdk.ModifierType modifier)
		{
//			if (Data != null) {
//				Console.WriteLine ("Data is not null at start of ViMode.HandleKeypress");
//				Console.WriteLine (CurState);
//				Console.WriteLine ("unicodeKey: {0}", (char)unicodeKey);
//			}

			// Reset on Esc, Ctrl-C, Ctrl-[
			if (key == Gdk.Key.Escape) {
				if (currentMacro != null) {
					// Record Escapes into the macro since it actually does something
					ViMacro.KeySet toAdd = new ViMacro.KeySet();
					toAdd.Key = key;
					toAdd.Modifiers = modifier;
					toAdd.UnicodeKey = unicodeKey;
					currentMacro.KeysPressed.Enqueue(toAdd);
				}

				if (PrevState == State.Change && CurState == State.Insert) {
					LastInsert.Clear ();
					LastInsert.AddRange(InsertBuffer);
					InsertBuffer.Clear ();
					LastAction.Add (RepeatLastInsert);
				} else if (CurState == State.Insert) {
					LastInsert.Clear ();
					LastInsert.AddRange(InsertBuffer);
					InsertBuffer.Clear ();
					if (!lastActionStarted) {
						lastActionStarted = true;
						LastAction.Clear ();
					}
					LastAction.Add (RepeatLastInsert);
				}
				Reset(string.Empty);
				return;
			} else if (((key == Gdk.Key.c || key == Gdk.Key.bracketleft) && (modifier & Gdk.ModifierType.ControlMask) != 0)) {
				Reset (string.Empty);
				if (currentMacro != null) {
					// Otherwise remove the macro from the pool
					macros.Remove(currentMacro.MacroCharacter);
					currentMacro = null;
				}
				return;
			} else if (currentMacro != null && !((char)unicodeKey == 'q' && modifier == Gdk.ModifierType.None)) {
				ViMacro.KeySet toAdd = new ViMacro.KeySet();
				toAdd.Key = key;
				toAdd.Modifiers = modifier;
				toAdd.UnicodeKey = unicodeKey;
				currentMacro.KeysPressed.Enqueue(toAdd);
			}
			
			Action<TextEditorData> action = null;
			bool lineAction = false;

			//handle numeric keypress
			if (AcceptNumericPrefix && '0' <= (char)unicodeKey && (char)unicodeKey <= '9') {
				numericPrefix += (char)unicodeKey;
				return;
			}
			
			switch (CurState) {
			case State.Unknown:
				Reset (string.Empty);
				goto case State.Normal;
			case State.Normal:
				if (((modifier & (Gdk.ModifierType.ControlMask)) == 0)) {
					if (key == Gdk.Key.Delete)
						unicodeKey = 'x';
					switch ((char)unicodeKey) {
					case '?':
					case '/':
					case ':':
						CurState = State.Command;
						commandBuffer.Append ((char)unicodeKey);
						Status = commandBuffer.ToString ();
						return;
					
					case 'A':
						StartNewLastAction (CaretMoveActions.LineEnd);
						RunAction (CaretMoveActions.LineEnd);
						goto case 'i';
						
					case 'I':
						StartNewLastAction (CaretMoveActions.LineFirstNonWhitespace);
						RunAction (CaretMoveActions.LineFirstNonWhitespace);
						goto case 'i';
					
					case 'a':
						StartNewLastAction (CaretMoveActions.Right);
						//use CaretMoveActions so that we can move past last character on line end
						RunAction (CaretMoveActions.Right);
						goto case 'i';

					case 'i':
						Caret.Mode = CaretMode.Insert;
						Status = "-- INSERT --";
						CurState = State.Insert;
						return;
						
					case 'R':
						Caret.Mode = CaretMode.Underscore;
						Status = "-- REPLACE --";
						CurState = State.Replace;
						return;

					case 'V':
						Status = "-- VISUAL LINE --";
						Data.SetSelectLines (Caret.Line, Caret.Line);
						CurState = State.VisualLine;
						return;
						
					case 'v':
						Status = "-- VISUAL --";
						CurState = State.Visual;
						RunAction (ViActions.VisualSelectionFromMoveAction (ViActions.Right));
						return;
						
					case 'd':
						Status = "d";
						CurState = State.Delete;
						return;
						
					case 'y':
						Status = "y";
						CurState = State.Yank;
						return;

					case 'Y':
						CurState = State.Yank;
						HandleKeypress (Gdk.Key.y, (int)'y', Gdk.ModifierType.None);
						return;
						
					case 'O':
						StartNewLastAction (ViActions.NewLineAbove);
						RunAction (ViActions.NewLineAbove);
						goto case 'i';
						
					case 'o':
						StartNewLastAction (ViActions.NewLineBelow);
						RunAction (ViActions.NewLineBelow);
						goto case 'i';
						
					case 'r':
						Caret.Mode = CaretMode.Underscore;
						Status = "-- REPLACE --";
						CurState = State.WriteChar;
						return;
						
					case 'c':
						Caret.Mode = CaretMode.Insert;
						Status = "c";
						CurState = State.Change;
						return;
						
					case 'x':
						if (Data.Caret.Column == Data.Document.GetLine (Data.Caret.Line).Length + 1)
							return;
						Status = string.Empty;
						if (!Data.IsSomethingSelected)
							RunActions (SelectionActions.FromMoveAction (CaretMoveActions.Right), ClipboardActions.Cut);
						else
							RunAction (ClipboardActions.Cut);
						ViActions.RetreatFromLineEnd (Data);
						return;
						
					case 'X':
						if (Data.Caret.Column == DocumentLocation.MinColumn)
							return;
						Status = string.Empty;
						if (!Data.IsSomethingSelected && 0 < Caret.Offset)
							RunActions (SelectionActions.FromMoveAction (CaretMoveActions.Left), ClipboardActions.Cut);
						else
							RunAction (ClipboardActions.Cut);
						return;
						
					case 'D':
						RunActions (SelectionActions.FromMoveAction (CaretMoveActions.LineEnd), ClipboardActions.Cut);
						return;
						
					case 'C':
						RunActions (SelectionActions.FromMoveAction (CaretMoveActions.LineEnd), ClipboardActions.Cut);
						goto case 'i';
						
					case '>':
						Status = ">";
						CurState = State.Indent;
						return;
						
					case '<':
						Status = "<";
						CurState = State.Unindent;
						return;
					case 'n':
						Search ();
						return;
					case 'N':
						searchBackward = !searchBackward;
						Search ();
						searchBackward = !searchBackward;
						return;
					case 'p':
						PasteAfter (false);
						return;
					case 'P':
						PasteBefore (false);
						return;
					case 's':
						if (!Data.IsSomethingSelected)
							RunAction (SelectionActions.FromMoveAction (CaretMoveActions.Right));
						RunAction (ClipboardActions.Cut);
						goto case 'i';
					case 'S':
						if (!Data.IsSomethingSelected)
							RunAction (SelectionActions.LineActionFromMoveAction (CaretMoveActions.LineEnd));
						else
							Data.SetSelectLines (Data.MainSelection.Anchor.Line, Data.Caret.Line);
						RunAction (ClipboardActions.Cut);
						goto case 'i';
						
					case 'g':
						Status = "g";
						CurState = State.G;
						return;
					
					case 'G':
						RunAction (marks ['`'].SaveMark);
						if (repeatCount > 1) {
							Caret.Line = repeatCount;
						} else {
							//RunAction (CaretMoveActions.ToDocumentEnd);
							RunAction (ToDocumentEnd);
						}
						Reset (string.Empty);
						return;
						
					case 'H':
						Caret.Line = System.Math.Max (DocumentLocation.MinLine, Editor.PointToLocation (0, Editor.LineHeight - 1).Line);
						return;
					case 'J':
						RunAction (ViActions.Join);
						return;
					case 'L':
						int line = Editor.PointToLocation (0, Editor.Allocation.Height - Editor.LineHeight * 2 - 2).Line;
						if (line < DocumentLocation.MinLine)
							line = Document.LineCount;
						Caret.Line = line;
						return;
					case 'M':
						line = Editor.PointToLocation (0, Editor.Allocation.Height / 2).Line;
						if (line < DocumentLocation.MinLine)
							line = Document.LineCount;
						Caret.Line = line;
						return;
						
					case '~':
						RunAction (ViActions.ToggleCase);
						return;
						
					case 'z':
						Status = "z";
						CurState = State.Fold;
						return;
						
					case 'm':
						Status = "m";
						CurState = State.Mark;
						return;
						
					case '`':
						Status = "`";
						CurState = State.GoToMark;
						return;
						
					case '@':
						Status = "@";
						CurState = State.PlayMacro;
						return;
	
					case 'q':
						if (currentMacro == null) {
							Status = "q";
							CurState = State.NameMacro;
							return;
						} 
						currentMacro = null;
						Reset ("Macro Recorded");
						return;
					case '*':
						SearchWordAtCaret (backward: false);
						return;
					case '#':
						SearchWordAtCaret (backward: true);
						return;
					case '.':
						RepeatLastAction ();
						return;
					}
					
				}

//				if (Data == null)
//					Console.WriteLine ("ViMode.HandleKeypress: after State.Normal block, Data is null");

				action = GetNavCharAction ((char)unicodeKey, true);
				if (action == null) {
					action = ViActionMaps.GetDirectionKeyAction (key, modifier);
				}
				if (action == null) {
					action = ViActionMaps.GetCommandCharAction ((char)unicodeKey);
				}

				if (action != null) {
					RunRepeatableAction (action);
				}

//				if (Data == null)
//					Console.WriteLine ("ViMode.HandleKeypress: right before CheckVisualMode, Data is null");
				
				//undo/redo may leave MD with a selection mode without activating visual mode
				CheckVisualMode ();
				return;
				
			case State.Delete:
				if (IsInnerOrOuterMotionKey (unicodeKey, ref motion))
					return;
				int offsetz = Caret.Offset;

				if (motion != Motion.None) {
					action = ViActionMaps.GetEditObjectCharAction ((char)unicodeKey, motion);
				} else if (((modifier & (Gdk.ModifierType.ShiftMask | Gdk.ModifierType.ControlMask)) == 0
				          && (unicodeKey == 'd' || unicodeKey == 'j' || unicodeKey == 'k'))) {
					if (unicodeKey == 'k') {
						action = CaretMoveActions.Up;
					} else {
						action = CaretMoveActions.Down;
					}
					if (unicodeKey == 'j') {
						repeatCount += 1;
					} //get one extra line for yj
					lineAction	= true;
				} else {
					action = GetNavCharAction ((char)unicodeKey, false);
					if (action == null)
						action = ViActionMaps.GetDirectionKeyAction (key, modifier);
					if (action != null)
						action = SelectionActions.FromMoveAction (action);
				}

				if (action != null) {
					if (lineAction) {
						RunAction (CaretMoveActions.LineStart);
						SelectionActions.StartSelection (Data);
						for (int i = 0; i < repeatCount; i++) {
							RunAction (action);
						}
						SelectionActions.EndSelection (Data);
						numericPrefix = "";
					} else {
						RunRepeatableAction (action);
					}
					if (Data.IsSomethingSelected && !lineAction)
						offsetz = Data.SelectionRange.Offset;
					RunAction (ClipboardActions.Cut);
					Reset (string.Empty);
				} else {
					Reset ("Unrecognised motion");
				}
				Caret.Offset = offsetz;

				return;

			case State.Yank:
				if (IsInnerOrOuterMotionKey (unicodeKey, ref motion))
					return;
				int offset = Caret.Offset;

				if (motion != Motion.None) {
					action = ViActionMaps.GetEditObjectCharAction ((char)unicodeKey, motion);
				} else if (((modifier & (Gdk.ModifierType.ShiftMask | Gdk.ModifierType.ControlMask)) == 0
				         && (unicodeKey == 'y' || unicodeKey == 'j' || unicodeKey == 'k'))) {
					if (unicodeKey == 'k') {
						action = CaretMoveActions.Up;
					} else {
						action = CaretMoveActions.Down;
					}
					if (unicodeKey == 'j') {
						repeatCount += 1;
					} //get one extra line for yj
					lineAction	= true;
				} else {
					action = GetNavCharAction ((char)unicodeKey, false);
					if (action == null)
						action = ViActionMaps.GetDirectionKeyAction (key, modifier);
					if (action != null)
						action = SelectionActions.FromMoveAction (action);
				}
				
				if (action != null) {
					if (lineAction) {
						RunAction (CaretMoveActions.LineStart);
						SelectionActions.StartSelection (Data);
						for (int i = 0; i < repeatCount; i++) {
							RunAction (action);
						}
						SelectionActions.EndSelection (Data);
						numericPrefix = "";
					} else {
						RunRepeatableAction (action);
					}
					if (Data.IsSomethingSelected && !lineAction)
						offset = Data.SelectionRange.Offset;
					RunAction (ClipboardActions.Copy);
					Reset (string.Empty);
				} else {
					Reset ("Unrecognised motion");
				}
				Caret.Offset = offset;
				
				return;
				
			case State.Change:
				if (IsInnerOrOuterMotionKey (unicodeKey, ref motion))
					return;

				lastActionStarted = true;
				LastAction.Clear ();

				if (motion != Motion.None) {
					action = ViActionMaps.GetEditObjectCharAction ((char)unicodeKey, motion);
				}
				//copied from delete action
				else if (((modifier & (Gdk.ModifierType.ShiftMask | Gdk.ModifierType.ControlMask)) == 0
				         && unicodeKey == 'c')) {
					action = SelectionActions.LineActionFromMoveAction (CaretMoveActions.LineEnd);
					lineAction = true;
				} else {
					action = ViActionMaps.GetEditObjectCharAction ((char)unicodeKey);
					if (action == null)
						action = ViActionMaps.GetDirectionKeyAction (key, modifier);
					if (action != null)
						action = SelectionActions.FromMoveAction (action);
				}
				
				if (action != null) {
					List<Action<TextEditorData>> actions;
					if (lineAction) {   //cd or cj  -- delete lines moving downward
						actions = GenerateRepeatedActionList (
							action, ClipboardActions.Cut, CaretMoveActions.LineFirstNonWhitespace);
						actions.Add (ViActions.NewLineAbove);
					} else if (unicodeKey == 'j') {   //cj -- delete current line and line below
						repeatCount += 1;
						action = SelectionActions.LineActionFromMoveAction (CaretMoveActions.LineEnd);
						actions = GenerateRepeatedActionList (
							action, ClipboardActions.Cut, CaretMoveActions.LineFirstNonWhitespace);
						actions.Add (ViActions.NewLineAbove);
					} else if (unicodeKey == 'k') {   //ck -- delete current line and line above
						repeatCount += 1;
						actions = GenerateRepeatedActionList (
							CaretMoveActions.LineFirstNonWhitespace, ClipboardActions.Cut, action);
						actions.Add (ViActions.NewLineBelow);
					} else {
						actions = GenerateRepeatedActionList (action);
						actions.Add (ClipboardActions.Cut);
					}
					RunActions (actions.ToArray ());
					LastAction.AddRange (actions);
					Status = "-- INSERT --";
					PrevState = State.Change;
					CurState = State.Insert;
					Caret.Mode = CaretMode.Insert;
				} else {
					Reset ("Unrecognised motion");
				}
				
				return;
				
			case State.Insert:
			case State.Replace:
				action = ViActionMaps.GetInsertKeyAction (key, modifier);

				// Record InsertKeyActions
				if (action != null) {
					RunAction (action);
					InsertBuffer.Add (new Keystroke (key, unicodeKey, modifier));
					return;
				}

				// Clear InsertBuffer if DirectionKeyAction
				action = ViActionMaps.GetDirectionKeyAction (key, modifier);
				if (action != null) {
					RunAction (action);
					InsertBuffer.Clear ();
					return;
				}

				if (unicodeKey != 0) {
					InsertCharacter (unicodeKey);
					InsertBuffer.Add (new Keystroke (key, unicodeKey, modifier));
				}

				return;

			case State.VisualLine:
				if (key == Gdk.Key.Delete)
					unicodeKey = 'x';
				switch ((char)unicodeKey) {
				case 'p':
					PasteAfter (true);
					return;
				case 'P':
					PasteBefore (true);
					return;
				}
				action = GetNavCharAction ((char)unicodeKey, false);
				if (action == null) {
					action = ViActionMaps.GetDirectionKeyAction (key, modifier);
				}
				if (action == null) {
					action = ViActionMaps.GetCommandCharAction ((char)unicodeKey);
				}
				if (action != null) {
					RunAction (SelectionActions.LineActionFromMoveAction (action));
					return;
				}

				ApplyActionToSelection (modifier, unicodeKey);
				return;

			case State.Visual:
				if (IsInnerOrOuterMotionKey (unicodeKey, ref motion)) return;

				if (motion != Motion.None) {
					action = ViActionMaps.GetEditObjectCharAction((char) unicodeKey, motion);
					if (action != null) {
						RunAction (action);
						return;
					}
				}

				if (key == Gdk.Key.Delete)
					unicodeKey = 'x';
				switch ((char)unicodeKey) {
				case 'p':
					PasteAfter (false);
					return;
				case 'P':
					PasteBefore (false);
					return;
				}
				action = GetNavCharAction ((char)unicodeKey, false);
				if (action == null) {
					action = ViActionMaps.GetDirectionKeyAction (key, modifier);
				}
				if (action == null) {
					action = ViActionMaps.GetCommandCharAction ((char)unicodeKey);
				}
				if (action != null) {
					RunAction (ViActions.VisualSelectionFromMoveAction (action));
					return;
				}

				ApplyActionToSelection (modifier, unicodeKey);
				return;
				
			case State.Command:
				switch (key) {
				case Gdk.Key.Return:
				case Gdk.Key.KP_Enter:
					Status = RunExCommand (commandBuffer.ToString ());
					commandBuffer.Length = 0;
					//CurState = State.Normal;
					break;
				case Gdk.Key.BackSpace:
				case Gdk.Key.Delete:
				case Gdk.Key.KP_Delete:
					if (0 < commandBuffer.Length) {
						commandBuffer.Remove (commandBuffer.Length-1, 1);
						Status = commandBuffer.ToString ();
						if (0 == commandBuffer.Length)
							Reset (Status);
					}
					break;
				default:
					if(unicodeKey != 0) {
						commandBuffer.Append ((char)unicodeKey);
						Status = commandBuffer.ToString ();
					}
					break;
				}
				return;
				
			case State.WriteChar:
				if (unicodeKey != 0) {
					RunAction (SelectionActions.StartSelection);
					int   roffset = Data.SelectionRange.Offset;
					InsertCharacter ((char) unicodeKey);
					Reset (string.Empty);
					Caret.Offset = roffset;
				} else {
					Reset ("Keystroke was not a character");
				}
				return;
				
			case State.Indent:
				if (((modifier & (Gdk.ModifierType.ControlMask)) == 0 && unicodeKey == '>')) { //select current line to indent
					Action<TextEditorData> indentAction = MakeIndentAction (repeatCount);
					StartNewLastAction (indentAction);
					RunAction (indentAction);
					return;
				}
				
				action = GetNavCharAction ((char)unicodeKey, false);
				if (action == null)
					action = ViActionMaps.GetDirectionKeyAction (key, modifier);

				if (action != null) {
					// TODO: Stuff this into a delegate and go 
					var linesToIndent = repeatCount;
					Action<TextEditorData> indentAction = delegate(TextEditorData data) {
						List<Action<TextEditorData>> actions = new List<Action<TextEditorData>> ();
						//get away from LineBegin
						RunAction (CaretMoveActions.LineFirstNonWhitespace);
						int roffset = Data.SelectionRange.Offset;
						actions.Add (ViActions.Right);
						for (int i = 0; i < linesToIndent; i++) {
							actions.Add (SelectionActions.FromMoveAction (action));
						}
						actions.Add (MiscActions.IndentSelection);
						RunActions (actions.ToArray ());
						//set cursor to start of first line indented
						Caret.Offset = roffset;
						RunAction (CaretMoveActions.LineFirstNonWhitespace);
						Reset ("");
					};
					StartNewLastAction (indentAction);
					RunAction (indentAction);
				} else {
					Reset ("Unrecognised motion");
				}
				return;
				
			case State.Unindent:
				if (((modifier & (Gdk.ModifierType.ControlMask)) == 0 && ((char)unicodeKey) == '<')) { //select current line to indent
					// TODO: Stuff this into a delegate and go
					Action<TextEditorData> unindentAction = MakeUnindentAction (repeatCount);
					StartNewLastAction (unindentAction);
					RunAction (unindentAction);
					return;
				}
				
				action = GetNavCharAction ((char)unicodeKey, false);
				if (action == null)
					action = ViActionMaps.GetDirectionKeyAction (key, modifier);
				
				if (action != null) {
					// TODO: Stuff this into a delegate and go
					var linesToUnindent = repeatCount;
					Action<TextEditorData> unindentAction = delegate(TextEditorData data) {
						List<Action<TextEditorData>> actions = new List<Action<TextEditorData>> ();
						RunAction (CaretMoveActions.LineFirstNonWhitespace);
						int roffset = Data.SelectionRange.Offset;
						//get away from LineBegin
						actions.Add (ViActions.Right);
						for (int i = 0; i < linesToUnindent; i++) {
							actions.Add (SelectionActions.FromMoveAction (action));
						}
						actions.Add (MiscActions.RemoveIndentSelection);
						RunActions (actions.ToArray ());
						//set cursor to start of first line indented
						Caret.Offset = roffset;
						RunAction (CaretMoveActions.LineFirstNonWhitespace);
						Reset ("");
					};
					StartNewLastAction (unindentAction);
					RunAction (unindentAction);
				} else {
					Reset ("Unrecognised motion");
				}
				return;

			case State.G:

				if (((modifier & (Gdk.ModifierType.ControlMask)) == 0)) {
					switch ((char)unicodeKey) {
					case 'g':
						RunAction (marks ['`'].SaveMark);
						Caret.Offset = 0;
						Reset ("");
						return;
					}
				}
				Reset ("Unknown command");
				return;
				
			case State.Mark: {
				char k = (char)unicodeKey;
				ViMark mark = null;
					if (!char.IsLetterOrDigit(k) && !SpecialMarks.Contains(k)) {
					Reset ("Invalid Mark");
					return;
				}
				if (marks.ContainsKey(k)) {
					mark = marks [k];
				} else {
					mark = new ViMark(k);
					marks [k] = mark;
				}
				RunAction(mark.SaveMark);
				Reset("");
				return;
			}
			
			case State.NameMacro: {
				char k = (char) unicodeKey;
				if(!char.IsLetterOrDigit(k)) {
					Reset("Invalid Macro Name");
					return;
				}
				currentMacro = new ViMacro (k);
				currentMacro.KeysPressed = new Queue<ViMacro.KeySet> ();
				macros [k] = currentMacro;
				Reset("");
				return;
			}
			
			case State.PlayMacro: {
				char k = (char) unicodeKey;
				if (k == '@') 
					k = macros_lastplayed;
				if (macros.ContainsKey(k)) {
          int playCount = repeatCount;  //store repeat count in case macro changes it
					Reset ("");
					macros_lastplayed = k; // FIXME play nice when playing macros from inside macros?
					ViMacro macroToPlay = macros [k];
          for (int i = 0 ; i < playCount ; i++)
          {
            foreach (ViMacro.KeySet keySet in macroToPlay.KeysPressed) {
              HandleKeypress(keySet.Key, keySet.UnicodeKey, keySet.Modifiers); // FIXME stop on errors? essential with multipliers and nowrapscan
            }
          }
					/* Once all the keys have been played back, quickly exit. */
					return;
				} else {
					Reset ("Invalid Macro Name '" + k + "'");
					return;
				}
			}
			
			case State.GoToMark: {
					char k = (char)unicodeKey;
					if (marks.ContainsKey (k)) {
						if (k != '`') {
							RunAction (marks ['`'].SaveMark);
						}
						RunAction (marks [k].LoadMark);
						Reset ("");
					} else {
						Reset ("Unknown Mark");
					}
				return;
			}
				
			case State.Fold:
				{
					if (((modifier & (Gdk.ModifierType.ControlMask)) == 0)) {
						switch ((char)unicodeKey) {
						case 'A':
						// Recursive fold toggle
							action = FoldActions.ToggleFoldRecursive;
							break;
						case 'C':
						// Recursive fold close
							action = FoldActions.CloseFoldRecursive;
							break;
						case 'M':
						// Close all folds
							action = FoldActions.CloseAllFolds;
							break;
						case 'O':
						// Recursive fold open
							action = FoldActions.OpenFoldRecursive;
							break;
						case 'R':
						// Expand all folds
							action = FoldActions.OpenAllFolds;
							break;
						case 'a':
						// Fold toggle
							action = FoldActions.ToggleFold;
							break;
						case 'c':
						// Fold close
							action = FoldActions.CloseFold;
							break;
						case 'o':
						// Fold open
							action = FoldActions.OpenFold;
							break;
						default:
							Reset ("Unknown command");
							break;
						}
					
						if (null != action) {
							RunAction (action);
							Reset (string.Empty);
						}
					}
					return;
				}

			case State.Confirm:
				if (((modifier & (Gdk.ModifierType.ControlMask)) == 0)) {
					switch ((char)unicodeKey) {
					case 'y':
						Reset ("Yes");
						break;

					case 'n':
						Reset ("No");
						break;
					}
				}

				return;
			}
		}

		static bool IsInnerOrOuterMotionKey (uint unicodeKey, ref Motion motion)
		{
			if (unicodeKey == 'i') {
				motion = Motion.Inner;
				return true;
			} 
			if (unicodeKey == 'a') {
				motion = Motion.Outer;
				return true;
			}
			return false;
		}

		/// <summary>
		/// Runs an in-place replacement on the selection or the current line
		/// using the "pattern", "replacement", and "trailer" groups of match.
		/// </summary>
		public string RegexReplace (System.Text.RegularExpressions.Match match)
		{
			string line = null;
			var segment = TextSegment.Invalid;

			if (Data.IsSomethingSelected) {
				// Operate on selection
				line = Data.SelectedText;
				segment = Data.SelectionRange;
			} else {
				// Operate on current line
				var lineSegment = Data.Document.GetLine (Caret.Line);
				if (lineSegment != null)
					segment = lineSegment;
				line = Data.Document.GetTextBetween (segment.Offset, segment.EndOffset);
			}

			// Set regex options
			RegexOptions options = RegexOptions.Multiline;
			if (match.Groups ["trailer"].Success) {
				if (match.Groups ["trailer"].Value.Contains ("i")) {
					options |= RegexOptions.IgnoreCase;
				}
			}

			// Mogrify group backreferences to .net-style references
			string replacement = Regex.Replace (match.Groups["replacement"].Value, @"\\([0-9]+)", "$$$1", RegexOptions.Compiled);
			replacement = Regex.Replace (replacement, "&", "$$0", RegexOptions.Compiled);

			try {
				string newline = "";
				if (match.Groups ["trailer"].Success && match.Groups["trailer"].Value.Contains("g"))
				{
					newline = Regex.Replace (line, match.Groups["pattern"].Value, replacement, options);
				}
				else
				{
					Regex regex = new Regex(match.Groups["pattern"].Value, options);
					newline = regex.Replace(line, replacement, 1);
				}
				Data.Replace (segment.Offset, line.Length, newline);
				if (Data.IsSomethingSelected)
					Data.ClearSelection ();
				lastPattern = match.Groups["pattern"].Value;
				lastReplacement = replacement; 
			} catch (ArgumentException ae) {
				return string.Format("Replacement error: {0}", ae.Message);
			}

			return "Performed replacement.";
		}

		public void ApplyActionToSelection (Gdk.ModifierType modifier, uint unicodeKey)
		{
			RunAction (SaveSelectionRange);
			if (Data.IsSomethingSelected && (modifier & (Gdk.ModifierType.ControlMask)) == 0) {
				switch ((char)unicodeKey) {
				case 'x':
				case 'd':
					StartNewLastAction (MakeActionToApplyToLastSelection (ClipboardActions.Cut));
					RunAction (ClipboardActions.Cut);
					Reset ("Deleted selection");
					return;
				case 'y':
					int offset = Data.SelectionRange.Offset;
					RunAction (ClipboardActions.Copy);
					Reset ("Yanked selection");
					Caret.Offset = offset;
					return;
				case 's':
				case 'c':
					StartNewLastAction (MakeActionToApplyToLastSelection (ClipboardActions.Cut));
					RunAction (ClipboardActions.Cut);
					Caret.Mode = CaretMode.Insert;
					CurState = State.Insert;
					Status = "-- INSERT --";
					return;
				case 'S':
					Data.SetSelectLines (Data.MainSelection.Anchor.Line, Data.Caret.Line);
					goto case 'c';
					
				case '>':
					int roffset = Data.SelectionRange.Offset;
					if (true) {
						int lineStart, lineEnd;
						MiscActions.GetSelectedLines (Data, out lineStart, out lineEnd);
						int lines = lineEnd - lineStart + 1;
						StartNewLastAction (MakeIndentAction (lines));
					}
					RunAction (MiscActions.IndentSelection);
					Caret.Offset = roffset;
					Reset ("");
					return;
					
				case '<':
					roffset = Data.SelectionRange.Offset;
					if (true) {
						int lineStart, lineEnd;
						MiscActions.GetSelectedLines (Data, out lineStart, out lineEnd);
						int lines = lineEnd - lineStart + 1;
						StartNewLastAction (MakeUnindentAction (lines));
					}
					RunAction (MiscActions.RemoveIndentSelection);
					Caret.Offset = roffset;
					Reset ("");
					return;

				case ':':
					commandBuffer.Append (":");
					Status = commandBuffer.ToString ();
					CurState = State.Command;
					break;
				case 'J':
					StartNewLastAction (MakeActionToApplyToLastSelection (ViActions.Join));
					RunAction (ViActions.Join);
					Reset ("");
					return;
					
				case '~':
					StartNewLastAction (MakeActionToApplyToLastSelection (ViActions.ToggleCase));
					RunAction (ViActions.ToggleCase);
					Reset ("");
					return;
				
				case '=':
					FormatSelection ();
					Reset ("");
					return;
				}
			}
		}

		private string Search()
		{
			int next = (Caret.Offset + 1 > Document.TextLength) ?
				Caret.Offset : Caret.Offset + 1;
			SearchResult result = searchBackward?
				Editor.SearchBackward (Caret.Offset):
				Editor.SearchForward (next);
			Editor.HighlightSearchPattern = (null != result);
			if (null == result)
				return string.Format ("Pattern not found: '{0}'", Editor.SearchPattern);
			else {
				RunAction (marks ['`'].SaveMark);
				Caret.Offset = result.Offset;
			}
		
			return string.Empty;
		}

		/// <summary>
		/// Pastes the selection after the caret,
		/// or replacing an existing selection.
		/// </summary>
		private void PasteAfter (bool linemode)
		{
			TextEditorData data = Data;
			using (var undo = Document.OpenUndoGroup ()) {
				
				Gtk.Clipboard.Get (ClipboardActions.CopyOperation.CLIPBOARD_ATOM).RequestText 
					(delegate (Gtk.Clipboard cb, string contents) {
					if (contents == null)
						return;
					if (contents.EndsWith ("\r") || contents.EndsWith ("\n")) {
						// Line mode paste
						if (data.IsSomethingSelected) {
							// Replace selection
							RunAction (ClipboardActions.Cut);
							data.InsertAtCaret (data.EolMarker);
							int offset = data.Caret.Offset;
							data.InsertAtCaret (contents);
							if (linemode) {
								// Existing selection was also in line mode
								data.Caret.Offset = offset;
								RunAction (DeleteActions.FromMoveAction (CaretMoveActions.Left));
							}
							RunAction (CaretMoveActions.LineStart);
						} else {
							// Paste on new line
							RunAction (ViActions.NewLineBelow);
							RunAction (DeleteActions.FromMoveAction (CaretMoveActions.LineStart));
							data.InsertAtCaret (contents);
							RunAction (DeleteActions.FromMoveAction (CaretMoveActions.Left));
							RunAction (CaretMoveActions.LineStart);
						}
					} else {
						// Inline paste
						if (data.IsSomethingSelected) 
							RunAction (ClipboardActions.Cut);
						else RunAction (CaretMoveActions.Right);
						data.InsertAtCaret (contents);
						RunAction (ViActions.Left);
					}
					Reset (string.Empty);
				});
			}
		}

		/// <summary>
		/// Pastes the selection before the caret,
		/// or replacing an existing selection.
		/// </summary>
		private void PasteBefore (bool linemode)
		{
			TextEditorData data = Data;
			
			using (var undo = Document.OpenUndoGroup ()) {
				Gtk.Clipboard.Get (ClipboardActions.CopyOperation.CLIPBOARD_ATOM).RequestText 
					(delegate (Gtk.Clipboard cb, string contents) {
					if (contents == null)
						return;
					if (contents.EndsWith ("\r") || contents.EndsWith ("\n")) {
						// Line mode paste
						if (data.IsSomethingSelected) {
							// Replace selection
							RunAction (ClipboardActions.Cut);
							data.InsertAtCaret (data.EolMarker);
							int offset = data.Caret.Offset;
							data.InsertAtCaret (contents);
							if (linemode) {
								// Existing selection was also in line mode
								data.Caret.Offset = offset;
								RunAction (DeleteActions.FromMoveAction (CaretMoveActions.Left));
							}
							RunAction (CaretMoveActions.LineStart);
						} else {
							// Paste on new line
							RunAction (ViActions.NewLineAbove);
							RunAction (DeleteActions.FromMoveAction (CaretMoveActions.LineStart));
							data.InsertAtCaret (contents);
							RunAction (DeleteActions.FromMoveAction (CaretMoveActions.Left));
							RunAction (CaretMoveActions.LineStart);
						}
					} else {
						// Inline paste
						if (data.IsSomethingSelected) 
							RunAction (ClipboardActions.Cut);
						data.InsertAtCaret (contents);
						RunAction (ViActions.Left);
					}
					Reset (string.Empty);
				});
			}
		}

		protected enum State {
			Unknown = 0,
			Normal,
			Command,
			Delete,
			Yank,
			Visual,
			VisualLine,
			Insert,
			Replace,
			WriteChar,
			Change,
			Indent,
			Unindent,
			G,
			Fold,
			Mark,
			GoToMark,
			NameMacro,
			PlayMacro,
			Confirm
		}
	}

	public enum Motion {
		None = 0,
		Inner,
		Outer
	}
}
