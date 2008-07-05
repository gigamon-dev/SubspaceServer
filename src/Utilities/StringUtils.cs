using System;
using System.Collections.Generic;
using System.Text;

namespace SS.Utilities
{
    public static class StringUtils
    {
        public static string TrimWhitespaceAndExtras(this string str, params char[] characters)
        {
            Dictionary<char, char> characterLookup = new Dictionary<char,char>(characters.Length);
            foreach(char c in characters)
            {
                characterLookup.Add(c, c);
            }

            int startIndex = 0;
            int endIndex = str.Length - 1;

            while(char.IsWhiteSpace(str[startIndex]) || characterLookup.ContainsKey(str[startIndex]))
            {
                startIndex++;
            }
            
            while(char.IsWhiteSpace(str[endIndex]) || characterLookup.ContainsKey(str[endIndex]))
            {
                endIndex--;
            }

            return str.Substring(startIndex, endIndex-startIndex+1);
        }
    }
}
