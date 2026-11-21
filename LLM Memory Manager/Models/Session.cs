using System;
using System.Collections.Generic;
using System.Text;
using System.ComponentModel.DataAnnotations.Schema;

namespace LLM_Memory_Manager.Models
{
    public class Session
    {
        public int Session_ID { get; set; }
        public int User_ID { get; set; }
        public DateTime Session_Start { get; set; }
        public DateTime? Session_End { get; set; } // Nullable, çünkü seans bitmemiş olabilir

        /// <summary>Listede üst satır: "Sohbet #12".</summary>
        [NotMapped]
        public string Title => $"Sohbet #{Session_ID}";

        /// <summary>Listede alt satır: başlangıç tarihi ve saati.</summary>
        [NotMapped]
        public string DateText => Session_Start == default ? "Yeni Sohbet" : Session_Start.ToString("dd.MM.yyyy HH:mm");

        public override string ToString()
        {
            var dateText = Session_Start == default
                ? "Yeni Sohbet"
                : Session_Start.ToString("dd.MM.yyyy HH:mm");

            return $"Sohbet #{Session_ID} - {dateText}";
        }
    }
}
