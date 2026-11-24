using System;
using System.Collections.Generic;

namespace codeLess.Core
{
    public interface IWhenContext
    {
        bool GetValue(string key);
    }

    public static class WhenClauseEvaluator
    {
        public static bool Evaluate(string clause, IWhenContext context)
        {
            if (string.IsNullOrEmpty(clause)) return true;
            // TODO: parse and evaluate properly
            return false;
        }
    }
}
