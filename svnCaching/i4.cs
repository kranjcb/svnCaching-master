using System;
using System.Collections.Generic;
using System.Linq;

namespace i4
{
    public static class ExceptionExtensions
    {
        public static void Throw(this Exception ex)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex).Throw();
        }
    }
}