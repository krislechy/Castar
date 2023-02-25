using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Castar.Logger
{
    public interface ICLogger
    {
        void Log<T>(string message, TypeLog type) where T : class;
    }
}
