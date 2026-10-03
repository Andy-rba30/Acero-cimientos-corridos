using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Interop;
using Arba.Comun;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace StripFootingRebar
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ArmarCimientoCommand : IExternalCommand
    {
        /// <summary>Que hacer con la armadura que este add-in ya habia creado en los anfitriones seleccionados.</summary>
        private enum Existing
        {
            /// <summary>No hay armadura anterior (o el usuario cancelo).</summary>
            None,
            /// <summary>Borrar los conjuntos del add-in de cada anfitrion y armar de nuevo (las barras antiguas CC-... se migran antes para reconocerlas).</summary>
            DeleteAndRebuild,
            /// <summary>No tocar nada y armar encima (duplica).</summary>
            Keep,
            /// <summary>Solo migrar las barras anteriores al contrato (particion y origen nuevos); no se crea ninguna barra.</summary>
            MigrateOnly
        }

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            AppConfig cfg;
            try { cfg = AppConfig.Load(); }
            catch (Exception ex)
            {
                message = "No se pudo leer config.json (" + AppConfig.ConfigPath() + "): " + ex.Message;
                return Result.Failed;
            }

            IList<Element> hosts;
            try { hosts = GetHosts(uidoc); }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }

            if (hosts.Count == 0)
            {
                message = "No se selecciono ningun cimiento ni sobrecimiento (cimentacion estructural, viga de cimentacion, suelo estructural o muro estructural).";
                return Result.Cancelled;
            }

            var allTypes = RebarGenerator.AllBarTypes(doc);
            List<string> barTypes = allTypes.Select(b => b.Name).ToList();
            var diametersMm = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (RebarBarType bt in allTypes)
                diametersMm[bt.Name] = UnitUtils.ConvertFromInternalUnits(bt.BarNominalDiameter, UnitTypeId.Millimeters);
            if (barTypes.Count == 0)
            {
                message = "El proyecto no tiene ningun tipo de barra (RebarBarType). Carga una familia de armadura primero.";
                return Result.Failed;
            }
            var allHooks = RebarGenerator.AllHookTypes(doc);
            List<string> hookTypes = allHooks.Select(h => h.Name).ToList();
            // angulo de cada gancho (grados) para dibujarlo en el esquema de la seccion
            var hookAngles = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (RebarHookType h in allHooks)
            {
                double deg = 135;
                try { deg = Math.Round(h.HookAngle * 180 / Math.PI); } catch { }
                hookAngles[h.Name] = deg;
            }

            // --- 1. Analisis geometrico de cada elemento (solo lectura, sin transaccion) ---
            // un cimiento modelado como suelo (Floor) con esquinas se parte en tramos rectos
            var stripNotes = new List<string>();
            var single = hosts.SelectMany(h => HostAnalysis.AnalyzeAll(doc, h, cfg, stripNotes)).ToList();
            // los cimientos armables se encadenan por sus extremos en recorridos
            var items = HostAnalysis.Chained(doc, single, cfg, out List<string> chainNotes);
            chainNotes.InsertRange(0, stripNotes);

            // --- 2. Interfaz: el usuario revisa que se ha detectado y elige el armado ---
            var win = new RebarOptionsWindow(doc, cfg.Clone(), barTypes, diametersMm, hookTypes, hookAngles, items);
            try { new WindowInteropHelper(win).Owner = commandData.Application.MainWindowHandle; } catch { }
            bool? ok = win.ShowDialog();
            if (ok != true || win.Result == null) return Result.Cancelled;
            cfg = win.Result;

            // --- 3. Armadura anterior de este add-in en los anfitriones (contrato ARBA-comun) ---
            // Conjuntos propios: los que llevan ARBA - Origen = CIMIENTOS CORRIDOS. Barras antiguas:
            // particion CC-... anterior al contrato y sin origen (no se reconocen como propias hasta migrarlas).
            List<Element> buildHosts = DistinctHosts(items);
            var own = new Dictionary<ElementId, int>();
            var legacy = new HashSet<ElementId>();
            foreach (Element h in buildHosts)
            {
                try
                {
                    int n = ArbaOrigin.Find(doc, ArbaContract.CimientosCorridos, h).Count;
                    if (n > 0) own[h.Id] = n;
                }
                catch { }
                try { if (ArbaMigration.HasLegacy(doc, h, ArbaContract.CimientosCorridos)) legacy.Add(h.Id); }
                catch { }
            }
            Existing mode = Existing.None;
            if (own.Count > 0 || legacy.Count > 0)
            {
                mode = AskExisting(own, legacy);
                if (mode == Existing.None) return Result.Cancelled;
            }

            // --- 4. Armado ---
            var log = new List<string>();
            var contractNotes = new List<string>();
            int total = 0, armed = 0, rejected = 0, deletedSets = 0, deletedBars = 0, migratedSets = 0;
            // anfitriones cuya armadura anterior ya se ha borrado en una subtransaccion confirmada
            var cleaned = new HashSet<ElementId>();

            using (Transaction tx = new Transaction(doc, "Armar cimientos / sobrecimientos"))
            {
                tx.Start();

                // parametros compartidos del contrato (ARBA - Origen / Codigo, Metrado - Elemento) antes de la primera subtransaccion
                ArbaSharedParams.Ensure(doc, new[] { ArbaContract.Origen, ArbaContract.Codigo, ArbaContract.Elemento }, contractNotes);
                doc.Regenerate();

                if (mode == Existing.MigrateOnly)
                {
                    foreach (Element h in buildHosts)
                    {
                        if (!legacy.Contains(h.Id)) continue;
                        string tag = "[" + h.Id + " " + h.Name + "] ";
                        try
                        {
                            ArbaMigrationResult mr = ArbaMigration.MigrateHost(doc, h, ArbaContract.CimientosCorridos);
                            migratedSets += mr.Migradas;
                            log.Add(tag + "MIGRADO sin rearmar: " + mr.Migradas + " conjunto(s) con particion y origen del contrato" +
                                    (mr.Avisos.Count > 0 ? ". Avisos: " + string.Join(" | ", mr.Avisos) : ""));
                        }
                        catch (Exception ex)
                        {
                            log.Add(tag + "NO MIGRADO -> ERROR: " + ex.Message);
                        }
                    }
                    tx.Commit();
                    Report("Migracion de barras anteriores al contrato",
                           migratedSets + " conjunto(s) migrados en " + legacy.Count + " anfitrion(es). No se ha creado ninguna barra.",
                           chainNotes, contractNotes, log, 0);
                    return Result.Succeeded;
                }

                foreach (HostAnalysis item in items)
                {
                    string tag = item.Tag;
                    if (!item.CanBuild)
                    {
                        rejected++;
                        log.Add(tag + "SIN ARMAR -> " + item.Detail(cfg));
                        continue;
                    }

                    // Cada elemento se arma dentro de una subtransaccion. Si cualquier barra
                    // queda fuera del hormigon (red de seguridad), se deshace TODO lo creado
                    // para ese elemento (y el borrado de su armadura anterior): o se arma
                    // entero y bien, o no se toca.
                    using (SubTransaction sub = new SubTransaction(doc))
                    {
                        sub.Start();
                        BuildResult res = null;
                        string error = null;
                        var subCleaned = new List<ElementId>();
                        int subSets = 0, subBars = 0;
                        try
                        {
                            if (mode == Existing.DeleteAndRebuild)
                            {
                                foreach (Element h in HostsOf(item))
                                {
                                    if (cleaned.Contains(h.Id) || subCleaned.Contains(h.Id)) continue;
                                    // las barras CC-... anteriores al contrato pasan a reconocerse como propias antes de borrar
                                    if (legacy.Contains(h.Id)) ArbaMigration.MigrateHost(doc, h, ArbaContract.CimientosCorridos);
                                    subSets += ArbaOrigin.Delete(doc, ArbaContract.CimientosCorridos, h, out int bars);
                                    subBars += bars;
                                    subCleaned.Add(h.Id);
                                }
                                if (subSets > 0) doc.Regenerate();
                            }

                            res = RebarGenerator.Build(doc, item, cfg);
                            if (res.Safe && res.Created.Count > 0)
                            {
                                doc.Regenerate();
                                RebarGenerator.VerifyCreated(doc, item.Section, res);
                            }
                        }
                        catch (Exception ex)
                        {
                            error = ex.Message;
                        }

                        bool keep = error == null && res != null && res.Safe;
                        string desc = item.Detail(cfg);
                        if (keep)
                        {
                            sub.Commit();
                            armed++;
                            total += res.Created.Count;
                            cleaned.UnionWith(subCleaned);
                            deletedSets += subSets;
                            deletedBars += subBars;
                            string line = tag + desc + "  ->  " + res.Summary + " (" + res.Created.Count + " conjuntos)";
                            if (subSets > 0)
                                line += "  REARMADO: borrados antes " + subSets + " conjuntos (" + subBars + " barras) del add-in";
                            if (res.Failed.Count > 0)
                                line += "  INCOMPLETO, no se pudieron crear: " + string.Join(" | ", res.Failed);
                            if (res.Notes.Count > 0)
                                line += "  NOTAS: " + string.Join(" | ", res.Notes);
                            if (res.Warnings.Count > 0)
                                line += "  AVISOS: " + string.Join(" | ", res.Warnings);
                            log.Add(line);
                        }
                        else
                        {
                            sub.RollBack();
                            rejected++;
                            string kept = subSets > 0 ? " Su armadura anterior se conserva." : "";
                            if (error != null)
                                log.Add(tag + "SIN ARMAR -> ERROR: " + error + ". Se ha deshecho todo lo creado para este elemento." + kept);
                            else
                                log.Add(tag + "SIN ARMAR -> " + desc + ": barras fuera del hormigon, se ha deshecho todo el " +
                                        "elemento (" + res.Rejected.Count + "): " + string.Join(" | ", res.Rejected) + kept);
                        }
                    }
                }
                tx.Commit();
            }

            string headline = total + " conjuntos de armadura creados en " + armed + " de " + items.Count + " elemento(s) / tramo(s) / recorrido(s).";
            if (deletedSets > 0)
                headline += Environment.NewLine + "Borrados antes " + deletedSets + " conjuntos (" + deletedBars + " barras) de este add-in en " + cleaned.Count + " anfitrion(es).";
            Report("Armado de cimientos / sobrecimientos", headline, chainNotes, contractNotes, log, rejected);
            return Result.Succeeded;
        }

        /// <summary>Informe final: notas del analisis, avisos de los parametros del contrato y una linea por elemento.</summary>
        private static void Report(string title, string headline, List<string> chainNotes, List<string> contractNotes, List<string> log, int rejected)
        {
            var lines = new List<string>(chainNotes);
            if (contractNotes.Count > 0)
            {
                lines.Add("Parametros del contrato ARBA:");
                lines.AddRange(contractNotes.Select(n => "  - " + n));
            }
            lines.AddRange(log);
            var td = new TaskDialog(title)
            {
                MainInstruction = headline,
                MainContent = string.Join(Environment.NewLine, lines),
                FooterText = "Contrato ARBA-comun " + ArbaContract.Version + "  |  particion \"" + AppConfig.DefaultPartitionTemplate +
                             "\"  |  ARBA - Origen = " + ArbaContract.CimientosCorridos.Origin
            };
            if (rejected > 0)
            {
                td.MainInstruction += Environment.NewLine + "ATENCION: " + rejected +
                                      " elemento(s) SIN ARMAR (ver detalle). No se ha creado ninguna barra en ellos.";
                td.MainIcon = TaskDialogIcon.TaskDialogIconWarning;
            }
            td.Show();
        }

        /// <summary>
        /// Pregunta una vez que hacer con la armadura que este add-in ya tiene en los anfitriones:
        /// borrar y rearmar, conservar (se arma encima) o, si hay barras anteriores al contrato,
        /// migrarlas sin rearmar. Cancelar devuelve None.
        /// </summary>
        private static Existing AskExisting(Dictionary<ElementId, int> own, HashSet<ElementId> legacy)
        {
            ArbaPrefix p = ArbaContract.CimientosCorridos;
            var content = new List<string>();
            if (own.Count > 0)
                content.Add(own.Count + " anfitrion(es) con " + own.Values.Sum() + " conjunto(s) creados por este add-in (ARBA - Origen = " + p.Origin + ").");
            if (legacy.Count > 0)
                content.Add(legacy.Count + " anfitrion(es) con barras anteriores al contrato ARBA (particion " +
                            string.Join(" / ", p.Legacy.Select(l => l + "-...")) +
                            ", sin ARBA - Origen); no se reconocen como propias hasta migrarlas.");
            content.Add("");
            content.Add("Elige que hacer con esa armadura antes de armar.");

            var td = new TaskDialog("Armadura anterior del add-in")
            {
                MainInstruction = "Los elementos seleccionados ya tienen armadura de Cimientos/Sobrecimientos.",
                MainContent = string.Join(Environment.NewLine, content),
                CommonButtons = TaskDialogCommonButtons.Cancel,
                DefaultButton = TaskDialogResult.Cancel,
                AllowCancellation = true,
                MainIcon = TaskDialogIcon.TaskDialogIconWarning,
                FooterText = "Contrato ARBA-comun " + ArbaContract.Version
            };
            td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Borrar la armadura del add-in y rearmar",
                "En cada anfitrion se borran los conjuntos de este add-in (las barras antiguas se migran primero para reconocerlas) y se arma de nuevo. " +
                "Sin duplicados. Si un elemento no se puede armar, su armadura anterior se conserva.");
            td.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Conservar lo que hay y armar encima",
                "No se borra ni se migra nada: la armadura nueva se anade a la existente (quedara duplicada).");
            if (legacy.Count > 0)
                td.AddCommandLink(TaskDialogCommandLinkId.CommandLink3, "Migrar las barras antiguas sin rearmar",
                    "Solo actualiza la particion (\"" + ArbaContract.CatCimientos + " - " + p.Prefix + "-marca\", o la categoria del anfitrion real), " +
                    "ARBA - Origen, ARBA - Codigo y Metrado - Elemento de las barras existentes. No se crea ninguna barra en esta ejecucion.");

            switch (td.Show())
            {
                case TaskDialogResult.CommandLink1: return Existing.DeleteAndRebuild;
                case TaskDialogResult.CommandLink2: return Existing.Keep;
                case TaskDialogResult.CommandLink3: return Existing.MigrateOnly;
                default: return Existing.None;
            }
        }

        /// <summary>Anfitriones reales de un elemento de la lista: los de cada tramo si es un recorrido, si no el suyo.</summary>
        private static IEnumerable<Element> HostsOf(HostAnalysis item)
        {
            if (item.Chain != null) return item.Chain.Segs.Select(s => s.Item?.Host).Where(h => h != null);
            return item.Host != null ? new[] { item.Host } : new Element[0];
        }

        /// <summary>Anfitriones distintos de los elementos armables (un suelo partido en tramos cuenta una vez).</summary>
        private static List<Element> DistinctHosts(IList<HostAnalysis> items)
        {
            var seen = new HashSet<ElementId>();
            var list = new List<Element>();
            foreach (HostAnalysis it in items)
            {
                if (!it.CanBuild) continue;
                foreach (Element h in HostsOf(it))
                    if (seen.Add(h.Id)) list.Add(h);
            }
            return list;
        }

        private static IList<Element> GetHosts(UIDocument uidoc)
        {
            Document doc = uidoc.Document;
            var sel = uidoc.Selection.GetElementIds()
                .Select(id => doc.GetElement(id))
                .Where(IsCandidate)
                .ToList();
            if (sel.Count > 0) return sel;

            IList<Reference> refs = uidoc.Selection.PickObjects(
                ObjectType.Element, new HostFilter(),
                "Selecciona los cimientos o sobrecimientos a armar (cimentaciones, vigas de cimentacion, suelos o muros estructurales; todos los tramos del recorrido) y pulsa Finalizar");
            return refs.Select(r => doc.GetElement(r)).ToList();
        }

        /// <summary>Cimentaciones estructurales, vigas de cimentacion, cimientos modelados como suelo (Floor, incluidas las losas de cimentacion) y sobrecimientos modelados como muro (Wall).</summary>
        private static bool IsCandidate(Element e)
        {
            if (e == null || e.Category == null) return false;
            if (e is Floor || e is Wall) return true;
            long id = e.Category.Id.Value;
            return id == (long)BuiltInCategory.OST_StructuralFoundation || id == (long)BuiltInCategory.OST_StructuralFraming ||
                   id == (long)BuiltInCategory.OST_Floors || id == (long)BuiltInCategory.OST_Walls;
        }

        private class HostFilter : ISelectionFilter
        {
            public bool AllowElement(Element e) => IsCandidate(e);
            public bool AllowReference(Reference r, XYZ p) => false;
        }
    }
}
