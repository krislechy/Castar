using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Castar.BL.Settings.Autorun
{
    public interface IAutorun
    {
        bool IsStartUp();
        void RemoveFromStartUp();
        void AddToStartUp();
        void Dispose();
    }
}
