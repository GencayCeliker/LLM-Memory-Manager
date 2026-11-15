using System;
using System.Collections.Generic;
using System.Text;

namespace LLM_Memory_Manager.Models
{
    public class Session
    {
        public int Session_ID { get; set; }
        public int User_ID { get; set; }
        public DateTime Session_Start { get; set; }
        public DateTime? Session_End { get; set; } // Nullable, çünkü seans bitmemiş olabilir

        public override string ToString()
        {
            var dateText = Session_Start == default
                ? "Yeni Sohbet"
                : Session_Start.ToString("dd.MM.yyyy HH:mm");

            return $"Sohbet #{Session_ID} - {dateText}";
        }
    }
}
