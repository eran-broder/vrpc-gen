using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace vRPC.core
{
    public interface IGenerator
    {
        public IEnumerable<(string fileName, string content)> Generate(Type type);
    }

    

}
