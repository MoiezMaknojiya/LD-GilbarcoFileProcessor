using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ApiLibrary.Models
{
    public class LoginResponse
    {
        public int success { get; set; }
        public UserData? user { get; set; }
        public string? accessToken { get; set; }
        public int store_id { get; set; }
        public int pos_dept_id { get; set; }
    }
}
