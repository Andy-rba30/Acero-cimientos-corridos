using System;
using System.Text.RegularExpressions;

namespace FootingRebar
{
    /// <summary>
    /// Expande la plantilla del parametro Particion: {marca}, {id}, {tipo}, {familia},
    /// {conjunto} y {cara}. Los comodines vacios se eliminan con el separador que
    /// los precede o sigue ("VIG-{marca}" con marca vacia da "VIG" y no "VIG-").
    /// </summary>
    public static class PartitionName
    {
        public sealed class Source
        {
            public string Mark = "", Id = "", TypeName = "", FamilyName = "", SetName = "", Face = "";
        }

        public static string Expand(string template, Source s)
        {
            if (string.IsNullOrWhiteSpace(template)) return "";
            string mark = string.IsNullOrWhiteSpace(s.Mark) ? s.Id : s.Mark;
            string result = Regex.Replace(template, @"\{(\w+)\}", m =>
            {
                string key = m.Groups[1].Value.ToLowerInvariant();
                switch (key)
                {
                    case "marca": return mark ?? "";
                    case "id": return s.Id ?? "";
                    case "tipo": return s.TypeName ?? "";
                    case "familia": return s.FamilyName ?? "";
                    case "conjunto": return s.SetName ?? "";
                    case "cara": return s.Face ?? "";
                    default: return m.Value;
                }
            });
            // separadores huerfanos: dobles, al principio o al final
            result = Regex.Replace(result, @"([-_ /.]){2,}", "$1");
            result = Regex.Replace(result, @"^[-_ /.]+|[-_ /.]+$", "");
            return result.Trim();
        }

        /// <summary>Lista de comodines para la ayuda de la interfaz.</summary>
        public const string Help = "{marca} (Marca del elemento; si esta vacia, el Id), {id}, {tipo} (nombre del tipo), " +
                                   "{familia}, {conjunto} (nombre del juego de barras) y {cara} (superior / inferior / estribo).";
    }
}
