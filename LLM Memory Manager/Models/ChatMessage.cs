using System;
using System.Collections.Generic;
using System.Text;

namespace LLM_Memory_Manager.Models
{
    public class ChatMessage
    {
        public int Message_ID { get; set; }
        public int Session_ID { get; set; }
        public int User_ID { get; set; }
        public string? Role { get; set; }
        public string? Message_Content { get; set; }
        public int Token_Count { get; set; }
        public DateTime Created_At { get; set; }
    }
}
