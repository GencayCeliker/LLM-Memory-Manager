using System;
using System.Collections.Generic;
using System.Text;

namespace LLM_Memory_Manager.Models
{
    public class User
    {
        public int User_ID { get; set; }
        public string? Phone_Number { get; set; }
        public string? User_Name { get; set; }
        public DateTime Created_At { get; set; }
    }
}
