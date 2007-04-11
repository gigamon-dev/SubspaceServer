using System;
using System.Collections.Generic;
using System.Text;
using System.IO;
using SS.Utilities;

namespace SS.Core
{
    public delegate int APPFileFinderFunc(out string dest, string arena, string name);
    public delegate void APPReportErrFunc(string error);

    /// <summary>
    /// equivalent of app.c/h
    /// 
    /// app - the [a]sss [p]re[p]rocessor
    /// 
    /// handles selected features of the C preprocessor, including #include,
    /// #define, #if[n]def, #else, #endif.
    /// 
    /// initial ; or / for comments.
    ///
    /// macros are _not_ expanded automatically. use $macro, $(macro), or
    /// ${macro} to get their values. macro names are case-insensitive.
    /// macros can't take arguments.
    /// </summary>
    public class APPContext : IDisposable
    {
        private const char DIRECTIVECHAR = '#';
        private const char CONTINUECHAR = '\\';
        private readonly char[] COMMENTCHARS = { '/', ';' };

        private const int MAX_RECURSION_DEPTH = 50;

        private class FileEntry
        {
            public StreamReader file;
            public string fname;
            public int lineno;
            public FileEntry prev; // i found out this is a type of stack

            public FileEntry(StreamReader file, string fname, int lineno)
            {
                this.file = file;
                this.fname = fname;
                this.lineno = lineno;
            }

            public void Close()
            {
                if (file != null)
                {
                    file.Dispose();
                    file = null;
                }
            }
        }

        private class IfBlock
        {
            public enum WhereType
            {
                in_if = 0,
                in_else = 1
            }

            public WhereType Where;

            public enum CondType
            {
                is_false = 0,
                is_true = 1
            }
            public CondType Cond;

            public IfBlock Prev; // i found out this is a type of stack

            public IfBlock()
            {
            }
        }

    	private APPFileFinderFunc finder;
        private APPReportErrFunc err;
        private string arena;
        private FileEntry file;
        private IfBlock ifs;
        //private LinkedList<FileEntry> fileList;
        //private LinkedList<IfBlock> ifsList;
        private bool processing;
        private Dictionary<string, string> defs;  // figure out what this stores
        private int depth;

        public APPContext(APPFileFinderFunc finder, APPReportErrFunc err, string arena)
        {
            this.finder = finder;
            this.err = err;
            this.arena = arena; // can be null
            processing = true;
            defs = new Dictionary<string, string>();
            depth = 0;
        }

        /// <summary>
        /// adds #defines
        /// </summary>
        /// <param name="key"></param>
        /// <param name="value"></param>
        public void AddDef(string key, string value)
        {
            defs[key] = value;
        }

        /// <summary>
        /// removes #defines
        /// </summary>
        /// <param name="key"></param>
        public void RemoveDef(string key)
        {
            defs.Remove(key);
        }

        /// <summary>
        /// adds a file for this context object to read from, to the end of the 'file' stack
        /// </summary>
        /// <param name="name">name of file to open</param>
        public void AddFile(string name)
        {

            if (depth >= MAX_RECURSION_DEPTH)
            {
                do_error("Maximum #include recursion depth reached while adding '{0}'", name);
                return;
            }

            FileEntry fe = get_file(name);
            if (fe == null)
            {
                return;
            }

            if (file == null)
            {
                file = fe;
            }
            else
            {
                FileEntry tfe = file;
                while (tfe.prev != null)
                {
                    tfe = tfe.prev;
                }
                tfe.prev = fe;

            }

            depth++;
        }

        /// <summary>
        /// To read the next line.
        /// </summary>
        /// <param name="buf">buffer to store the line in</param>
        /// <returns>false on eof</returns>
        public bool GetLine(out string buf)
        {
            StringBuilder sb = new StringBuilder();

            while (true)
            {
                sb.Length = 0;

                while (true)
                {
                    // first find an actual line to process
                    while (true)
                    {
                        if (file == null)
                        {
                            buf = null;
                            return false;
                        }

                        string line = file.file.ReadLine();
                        if (line == null)
                        {
                            // we hit eof on this file, pop it off and try next
                            FileEntry of = file;
                            file = of.prev;
                            of.Close();
                            depth--;
                        }
                        else
                        {
                            sb.Append(line);
                            break;
                        }
                    }

                    file.lineno++;
                    if ((sb.Length > 0) && (sb[sb.Length - 1] == CONTINUECHAR))
                        sb.Length = sb.Length - 1;
                    else
                        break;
                }

                string trimmedSb = sb.ToString().Trim();

                // check for directives
                if ((trimmedSb != string.Empty) && (trimmedSb[0] == DIRECTIVECHAR))
                {
                    handle_directive(trimmedSb);
                    continue;
                }
                else
                {
                    // then comments and empty lines
                    if ((trimmedSb == string.Empty) || (trimmedSb.IndexOfAny(COMMENTCHARS) != -1))
                        continue;
                }

                // here we have an actual line
                // if we're not processing it, skip it
                if (processing)
                {
                    buf = trimmedSb;
                    return true;
                }
            }
        }

        private FileEntry get_file(string name)
        {
            string fname;
            if (finder(out fname, arena, name) == -1)
            {
                do_error("Can't find file for arena '{0}', name '{1}'", arena, name);
                return null;
            }

            StreamReader sr = null;
            try
            {
                sr = new StreamReader(fname);
            }
            catch(Exception ex)
            {
                do_error("Can't open file '{0}' for reading: {1}", fname, ex.Message);
		        return null;
            }

            return new FileEntry(sr, fname, 0);
        }

        private void update_processing()
        {
            IfBlock i = ifs;

            processing = true;
            while (i != null)
            {
                if ((int)i.Cond == (int)i.Where)
                {
                    processing = false;
                }
                i = i.Prev;
            }
        }

        private void push_if(bool cond)
        {
            IfBlock i = new IfBlock();
            i.Cond = cond ? IfBlock.CondType.is_true : IfBlock.CondType.is_false;
            i.Where = IfBlock.WhereType.in_if;
            i.Prev = ifs;
            ifs = i;

	        update_processing();
        }

        private void pop_if()
        {
            if (ifs != null)
            {
                ifs = ifs.Prev;
            }
            else
            {
                do_error("No #if blocks to end ({0}:{1})", file.fname, file.lineno);
            }

            update_processing();
        }

        private void switch_if()
        {
            if (ifs != null)
            {
                if (ifs.Where == IfBlock.WhereType.in_if)
                    ifs.Where = IfBlock.WhereType.in_else;
                else
                    do_error("Multiple #else directives ({0}:{1})", file.fname, file.lineno);
            }
            else
                do_error("Unexpected #else directive ({0}:{1})", file.fname, file.lineno);

            update_processing();
        }

        private void handle_directive(string buf)
        {
            /* i dont like how i'd have to convert this if i did it line for line
            // skip DIRECTIVECHAR
            buf = buf.Substring(1);

            if (buf.StartsWith("ifdef", StringComparison.InvariantCultureIgnoreCase) ||
                buf.StartsWith("ifndef", StringComparison.InvariantCultureIgnoreCase))
            {
                string t = trimWhitespaceAndExtras(buf.Substring(6), '{', '}', '(', ')');
                bool cond = defs.ContainsKey(t);
                push_if(buf[2] == 'd' ? cond : !cond);
            }
            else if (buf.StartsWith("else", StringComparison.InvariantCultureIgnoreCase))
            {
                switch_if();
            }
            else if (buf.StartsWith("endif", StringComparison.InvariantCultureIgnoreCase))
            {
                pop_if();
            }
            else
            {
                // now handle the stuff valid while processing
                if (!processing)
                {
                    return;
                }

                if (buf.StartsWith("define", StringComparison.InvariantCultureIgnoreCase))
                {

                }
            }
            */
            char[] separators = new char[]{' ', '\t'};
            string[] tokens = buf.Split(separators, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
                return; // bad

            string directiveLower = tokens[0].Trim().ToLower();

            switch (directiveLower)
            {
                case "#ifdef":
                case "#ifndef":
                    if (tokens.Length < 2)
                        return; // bad

                    string t = StringUtils.TrimWhitespaceAndExtras(tokens[1], '{', '}', '(', ')');
                    bool cond = defs.ContainsKey(t);
                    push_if(buf[2] == 'd' ? cond : !cond);
                    break;

                case "#else":
                    switch_if();
                    break;

                case "#endif":
                    pop_if();
                    break;
            }

            if (!processing)
                return;

            string key = null;
            string value = null;

            switch (directiveLower)
            {
                case "#define": // ex: #define SOMEKEY SOMEVALUE
                    if (tokens.Length == 2)
                    {
                        // define with no value
                        key = tokens[1].Trim();
                        AddDef(key, "1");
                    }
                    else if (tokens.Length >= 3)
                    {
                        // define with value
                        key = tokens[1].Trim();
                        value = tokens[2].Trim();
                        AddDef(key, value);
                    }
                    else
                    {
                        return; // bad
                    }
                    break;

                case "#undef":
                    if (tokens.Length < 2)
                        return; // bad
                    key = tokens[1].Trim();
                    RemoveDef(key);
                    break;

                case "#include":
                    tokens = buf.Split(separators, 2, StringSplitOptions.RemoveEmptyEntries);
                    if (tokens.Length != 2)
                        return; // bad

                    string filename = StringUtils.TrimWhitespaceAndExtras(tokens[1], '"', '<', '>');
                    FileEntry fe = get_file(filename);
                    if (fe != null)
                    {
                        fe.prev = file;
                        file = fe;
                    }
                    break;
            }
        }

        private void do_error(string format, params object[] args)
        {
            if (err != null)
                err(string.Format(format, args));
        }

        #region IDisposable Members

        /// <summary>
        /// use for cleanup, makes sure all files are closed among other things
        /// </summary>
        public void Dispose()
        {
            defs.Clear();
            defs = null;

            FileEntry f = file;
            while (f != null)
            {
                f.Close();
                f = f.prev;
            }
            file = null;

            ifs = null;
        }

        #endregion
    }
}
