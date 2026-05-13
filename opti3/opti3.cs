using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using VMS.TPS.Common.Model.API;
using VMS.TPS.Common.Model.Types;

[assembly: ESAPIScript(IsWriteable = true)]

namespace VMS.TPS
{
    public class Script
    {
        public Script()
        {
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Execute(ScriptContext context)
        {
            if (context == null || context.Patient == null)
            {
                MessageBox.Show("No patient selected. Abre un paciente y vuelve a correr el script.");
                return;
            }

            if (context.StructureSet == null)
            {
                MessageBox.Show("No hay StructureSet activo. Abre/selecciona un StructureSet y vuelve a correr el script.");
                return;
            }

            var ss = context.StructureSet;
            var notes = new List<string>();
            var temporaryStructures = new List<Structure>();
            string stage = "inicio";
            try
            {

                // =========================================================
                // CONFIGURACION MANUAL TEMPORAL
                // Luego esto vendra desde el ViewModel / WPF.
                // ptv1 = mayor volumen / menor dosis
                // ptv2 = volumen intermedio / dosis intermedia
                // ptv3 = boost / mayor dosis (opcional)
                // =========================================================
                string ptv1Id = "PTV_45Gy";
                string ptv2Id = "PTV_55Gy";
                string ptv3Id = "PTV_57.5Gy"; // dejar "" o null si no existe

                bool cropPtv1OutsideBody = false;
                double bodyCropMm = 4.0;

                string ringPtv1Id = BuildRingIdFromPtvId(ptv1Id);
                string ringPtv2Id = BuildRingIdFromPtvId(ptv2Id);
                string ringPtv3Id = BuildRingIdFromPtvId(ptv3Id);

                const double ptvExclusionMm = 4.0;
                const double rvrOuterMm = 25.0;
                const double rvrInnerMm = 5.0;
                const double rvrBodyCropMm = 4.0;
                const double ringOuterMm = 1.0;
                const double ringInnerMm = 4.0;
                const double oarMarginMm = 4.0;
                const double spinalCordSymmetricMm = 7.0;
                const double spinalCordPosteriorExtraMm = 8.0;
                const double femoralRingCropMm = 4.0;

                // =========================================================
                // MATCH DE ESTRUCTURAS FUENTE
                // =========================================================
                var ptv1 = FindStructureById(ss, ptv1Id);
                var ptv2 = FindStructureById(ss, ptv2Id);
                var ptv3 = FindStructureById(ss, ptv3Id);

                if (ptv1 == null)
                {
                    MessageBox.Show("No se encontro ptv1: '" + ptv1Id + "'.");
                    return;
                }

                if (ptv2 == null)
                {
                    MessageBox.Show("No se encontro ptv2: '" + ptv2Id + "'.");
                    return;
                }

                var body = FindExternalStructure(ss);
                if (body == null)
                {
                    MessageBox.Show("No se encontro la estructura EXTERNAL / Body.");
                    return;
                }

                // Validar que los PTV que se van a editar realmente sean editables.
                string editError;
                if (!ptv1.CanEditSegmentVolume(out editError))
                {
                    MessageBox.Show("ptv1 no se puede editar: " + editError);
                    return;
                }

                if (!ptv2.CanEditSegmentVolume(out editError))
                {
                    MessageBox.Show("ptv2 no se puede editar: " + editError);
                    return;
                }

                if (ptv3 != null && !ptv3.CanEditSegmentVolume(out editError))
                {
                    MessageBox.Show("ptv3 no se puede editar: " + editError);
                    return;
                }

                // OAR aliases para el prematch actual.
                var aliases = BuildAliases();

                var bladder = FindFirstExistingStructure(ss, aliases["Bladder"]);
                var rectum = FindFirstExistingStructure(ss, aliases["Rectum"]);
                var colonSigmoid = FindFirstExistingStructure(ss, aliases["ColonSigmoid"]);
                var spinalCord = FindFirstExistingStructure(ss, aliases["SpinalCord"]);
                var caudaEquina = FindFirstExistingStructure(ss, aliases["CaudaEquina"]);
                var kidneyL = FindFirstExistingStructure(ss, aliases["KidneyL"]);
                var kidneyR = FindFirstExistingStructure(ss, aliases["KidneyR"]);
                var femoralHeadL = FindFirstExistingStructure(ss, aliases["FemoralHeadL"]);
                var femoralHeadR = FindFirstExistingStructure(ss, aliases["FemoralHeadR"]);

                context.Patient.BeginModifications();
                // =========================================================
                // WORKSPACE TEMPORAL PARA BOOLEANOS
                // Si alguna estructura fuente es HR, todas las copias de trabajo
                // se convierten a HR. Las estructuras originales NO se convierten.
                // =========================================================
                stage = "crear workspace temporal compatible";

                bool useHighResolutionWorkspace = AnyHighResolution(new[]
                {
                    ptv1, ptv2, ptv3,
                    body,
                    bladder, rectum, colonSigmoid,
                    spinalCord, caudaEquina,
                    kidneyL, kidneyR,
                    femoralHeadL, femoralHeadR
                });

                if (useHighResolutionWorkspace)
                    notes.Add("Se detectaron estructuras High Resolution. Se usaron copias temporales HR para las operaciones booleanas.");

                // Copias temporales para operaciones booleanas
                var wPtv1 = CreateTemporaryCopy(ss, ptv1, "zzTmp001", useHighResolutionWorkspace, temporaryStructures, notes);
                var wPtv2 = CreateTemporaryCopy(ss, ptv2, "zzTmp002", useHighResolutionWorkspace, temporaryStructures, notes);
                var wPtv3 = ptv3 != null ? CreateTemporaryCopy(ss, ptv3, "zzTmp003", useHighResolutionWorkspace, temporaryStructures, notes) : null;

                var wBody = CreateTemporaryCopy(ss, body, "zzTmp004", useHighResolutionWorkspace, temporaryStructures, notes);

                var wBladder = CreateTemporaryCopy(ss, bladder, "zzTmp005", useHighResolutionWorkspace, temporaryStructures, notes);
                var wRectum = CreateTemporaryCopy(ss, rectum, "zzTmp006", useHighResolutionWorkspace, temporaryStructures, notes);
                var wColonSigmoid = CreateTemporaryCopy(ss, colonSigmoid, "zzTmp007", useHighResolutionWorkspace, temporaryStructures, notes);

                var wSpinalCord = CreateTemporaryCopy(ss, spinalCord, "zzTmp008", useHighResolutionWorkspace, temporaryStructures, notes);
                var wCaudaEquina = CreateTemporaryCopy(ss, caudaEquina, "zzTmp009", useHighResolutionWorkspace, temporaryStructures, notes);

                var wKidneyL = CreateTemporaryCopy(ss, kidneyL, "zzTmp010", useHighResolutionWorkspace, temporaryStructures, notes);
                var wKidneyR = CreateTemporaryCopy(ss, kidneyR, "zzTmp011", useHighResolutionWorkspace, temporaryStructures, notes);

                var wFemoralHeadL = CreateTemporaryCopy(ss, femoralHeadL, "zzTmp012", useHighResolutionWorkspace, temporaryStructures, notes);
                var wFemoralHeadR = CreateTemporaryCopy(ss, femoralHeadR, "zzTmp013", useHighResolutionWorkspace, temporaryStructures, notes);


                // =========================================================
                // CORTAR PTV1 FUERDA DE BODY SI ES "TRUE"
                // =========================================================
                stage = "crop ptv1 fuera de body";
                if (cropPtv1OutsideBody)
                {
                    ptv1.SegmentVolume = ptv1.SegmentVolume.And(Shrink(body, bodyCropMm));
                }
                // =========================================================
                // PREPARAR MARGENES DE PTVS FUENTE (ANTES DE MODIFICARLOS)
                // =========================================================
                SegmentVolume ptv1Plus4 = Expand(wPtv1, ptvExclusionMm);
                SegmentVolume ptv2Plus4 = Expand(wPtv2, ptvExclusionMm);
                SegmentVolume ptv3Plus4 = wPtv3 != null ? Expand(wPtv3, ptvExclusionMm) : null;

                // =========================================================
                // 1) z_RVR  (antes de modificar PTVs)
                // =========================================================
                stage = "crear z_RVR";
                var zRvr = RecreateStructureForWorkspace(ss, "CONTROL", "z_RVR", useHighResolutionWorkspace, notes);
                SegmentVolume rvrOuter = Expand(wPtv1, rvrOuterMm);
                SegmentVolume rvrInner = Expand(wPtv1, rvrInnerMm);
                SegmentVolume rvrVolume = rvrOuter.Sub(rvrInner);
                SegmentVolume bodyMinus4 = Shrink(wBody, rvrBodyCropMm);
                zRvr.SegmentVolume = rvrVolume.And(bodyMinus4);
                //zRvr.Color = Color.FromRgb(0, 191, 255);

                // Si hay boosts fuera de ptv1, recortar z_RVR contra boosts.
                zRvr.SegmentVolume = zRvr.SegmentVolume.Sub(ptv2Plus4);
                if (ptv3Plus4 != null)
                    zRvr.SegmentVolume = zRvr.SegmentVolume.Sub(ptv3Plus4);

                // =========================================================
                // 2) z_OARs  (antes de modificar PTVs)
                // =========================================================
                stage = "crear z_OARs";
                var zBladder = CreateExpandedOar(
                    ss,
                    "z_Bladder",
                    new[] { wBladder },
                    oarMarginMm,
                    new[] { ptv1Plus4, ptv2Plus4, ptv3Plus4 },
                    Color.FromRgb(255, 255, 0),
                    notes,
                    "Bladder",
                    useHighResolutionWorkspace);

                var zRectum = CreateExpandedOar(
                    ss,
                    "z_Rectum",
                    new[] { wRectum, wColonSigmoid },
                    oarMarginMm,
                    new[] { ptv1Plus4, ptv2Plus4, ptv3Plus4 },
                    Color.FromRgb(139, 69, 19),
                    notes,
                    "Rectum/Colon_Sigmoid",
                    useHighResolutionWorkspace);

                var zKidneys = CreateExpandedOar(
                    ss,
                    "z_Kidneys",
                    new[] { wKidneyL, wKidneyR },
                    oarMarginMm,
                    new[] { ptv1Plus4, ptv2Plus4, ptv3Plus4 },
                    Color.FromRgb(255, 255, 0),
                    notes,
                    "Kidney_L/Kidney_R",
                    useHighResolutionWorkspace);
                stage = "crear z_SpinalCord";
                var zSpinalCord = CreateSpinalCordStructure(
                    ss,
                    wSpinalCord,
                    wCaudaEquina,
                    spinalCordSymmetricMm,
                    spinalCordPosteriorExtraMm,
                    new[] { ptv1Plus4, ptv2Plus4, ptv3Plus4 },
                    notes,
                    useHighResolutionWorkspace);
                stage = "recortar z_RVR con z_OARs";
                // Cortar z_RVR contra los otros z_OAR con margen 0 mm.
                if (zBladder != null) zRvr.SegmentVolume = zRvr.SegmentVolume.Sub(zBladder);
                if (zRectum != null) zRvr.SegmentVolume = zRvr.SegmentVolume.Sub(zRectum);
                if (zKidneys != null) zRvr.SegmentVolume = zRvr.SegmentVolume.Sub(zKidneys);
                if (zSpinalCord != null) zRvr.SegmentVolume = zRvr.SegmentVolume.Sub(zSpinalCord);

                // =========================================================
                // 3) CORTAR PTVs (en el workspace)
                // =========================================================
                stage = "recortar PTVs";

                // Jerarquia: wPtv1 <- quitar wPtv2/wPtv3 ; wPtv2 <- quitar wPtv3
                wPtv1.SegmentVolume = wPtv1.SegmentVolume.Sub(ptv2Plus4);

                if (ptv3Plus4 != null)
                    wPtv1.SegmentVolume = wPtv1.SegmentVolume.Sub(ptv3Plus4);

                if (ptv3Plus4 != null)
                    wPtv2.SegmentVolume = wPtv2.SegmentVolume.Sub(ptv3Plus4);

                // Recalcular margenes con los PTV del workspace ya ajustados para la etapa de aros.
                ptv2Plus4 = Expand(wPtv2, ptvExclusionMm);
                ptv3Plus4 = wPtv3 != null ? Expand(wPtv3, ptvExclusionMm) : null;

                // =========================================================
                // 4) AROS
                // =========================================================
                stage = "crear aros";
                var ring1 = CreateRing(ss, wPtv1, ringPtv1Id, ringOuterMm, ringInnerMm, Color.FromRgb(255, 165, 0));
                var ring2 = CreateRing(ss, wPtv2, ringPtv2Id, ringOuterMm, ringInnerMm, Color.FromRgb(255, 0, 0));
                var ring3 = wPtv3 != null ? CreateRing(ss, wPtv3, ringPtv3Id, ringOuterMm, ringInnerMm, Color.FromRgb(178, 34, 34)) : null;

                // Cortar aros de menor dosis contra boosts de mayor dosis.
                if (ring1 != null)
                {
                    ring1.SegmentVolume = ring1.SegmentVolume.Sub(ptv2Plus4);
                    if (ptv3Plus4 != null)
                        ring1.SegmentVolume = ring1.SegmentVolume.Sub(ptv3Plus4);
                }

                if (ring2 != null && ptv3Plus4 != null)
                {
                    ring2.SegmentVolume = ring2.SegmentVolume.Sub(ptv3Plus4);
                }

                // Cortar los aros dentro de las femoral heads (4 mm).
                SegmentVolume femoralHeadLPlus4 = wFemoralHeadL != null ? Expand(wFemoralHeadL, femoralRingCropMm) : null;
                SegmentVolume femoralHeadRPlus4 = wFemoralHeadR != null ? Expand(wFemoralHeadR, femoralRingCropMm) : null;

                CropRingByFemoralHeads(ring1, femoralHeadLPlus4, femoralHeadRPlus4);
                CropRingByFemoralHeads(ring2, femoralHeadLPlus4, femoralHeadRPlus4);
                CropRingByFemoralHeads(ring3, femoralHeadLPlus4, femoralHeadRPlus4);

                if (femoralHeadL == null) notes.Add("No se encontro Femoral_Head_L / alias. Se omitio ese recorte de aros.");
                if (femoralHeadR == null) notes.Add("No se encontro Femoral_Head_R / alias. Se omitio ese recorte de aros.");
                if (ptv3 == null) notes.Add("No se encontro ptv3. El flujo corrio en modo 2 PTV.");

                //-----------aplicar los PTVs recortados------------
                stage = "copiar PTVs recortados desde workspace a originales";

                bool allowConvertOriginalPtvsToHighResolution = true;
                if (!allowConvertOriginalPtvsToHighResolution)
                {
                    notes.Add(
                        "ADVERTENCIA: No se recortaron los PTV originales porque tienen resoluciones mezcladas. " +
                        "Las estructuras de optimizacion y los aros se generaron desde copias temporales compatibles.");
                    return;
                }

                ApplyWorkspacePtvsToOriginals(
                    ptv1,
                    ptv2,
                    ptv3,
                    wPtv1,
                    wPtv2,
                    wPtv3,
                    allowConvertOriginalPtvsToHighResolution,
                    notes);


                // =========================================================
                // RESUMEN
                // =========================================================
                var sb = new StringBuilder();
                sb.AppendLine("OK: Se crearon/actualizaron las OptiStructures.");
                sb.AppendLine();
                sb.AppendLine("PTVs usados:");
                sb.AppendLine("- ptv1 = " + ptv1.Id);
                sb.AppendLine("- ptv2 = " + ptv2.Id);
                sb.AppendLine("- ptv3 = " + (ptv3 != null ? ptv3.Id : "(no hay ptv3)"));
                sb.AppendLine();

                if (notes.Any())
                {
                    sb.AppendLine("Observaciones:");
                    foreach (var note in notes.Distinct())
                        sb.AppendLine("- " + note);
                }
                else
                {
                    sb.AppendLine("No hubo observaciones.");
                }

                MessageBox.Show(sb.ToString(), "OptiStructures");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error en etapa: " + stage + "\n\n" + ex.Message, "OptiStructures");
            }
            finally
            {
                CleanupTemporaryStructures(ss, temporaryStructures);
            }
        }

        // =============================================================
        // HELPERS
        // =============================================================

        private static Dictionary<string, string[]> BuildAliases()
        {
            return new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                { "Bladder", new[] { "Bladder", "BLADDER" } },
                { "Rectum", new[] { "Rectum", "RECTUM" } },
                { "ColonSigmoid", new[] { "Colon_Sigmoid", "Sigmoid", "ColonSigmoid" } },
                { "SpinalCord", new[] { "SpinalCord", "Spinal_Cord", "Cord", "SPINALCORD" } },
                { "CaudaEquina", new[] { "CaudaEquina", "Cauda_Equina", "CAUDAEQUINA" } },
                { "KidneyL", new[] { "Kidney_L", "Kidney L", "Left Kidney", "LT Kidney" } },
                { "KidneyR", new[] { "Kidney_R", "Kidney R", "Right Kidney", "RT Kidney" } },
                { "FemoralHeadL", new[] { "Femoral_Head_L", "FemoralHead_L", "FemHead_L", "Left Femoral Head" } },
                { "FemoralHeadR", new[] { "Femoral_Head_R", "FemoralHead_R", "FemHead_R", "Right Femoral Head" } },
            };
        }

        private static Structure FindStructureById(StructureSet ss, string id)
        {
            if (ss == null || string.IsNullOrWhiteSpace(id))
                return null;

            return ss.Structures.FirstOrDefault(s =>
                s != null &&
                !string.IsNullOrWhiteSpace(s.Id) &&
                s.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        }

        private static string BuildRingIdFromPtvId(string ptvId)
        {
            if (string.IsNullOrWhiteSpace(ptvId))
                return null;

            return "z_" + ptvId.Trim() + "_A";
        }

        private static Structure FindFirstExistingStructure(StructureSet ss, IEnumerable<string> candidateIds)
        {
            if (ss == null || candidateIds == null)
                return null;

            foreach (var candidateId in candidateIds)
            {
                var match = FindStructureById(ss, candidateId);
                if (match != null)
                    return match;
            }

            return null;
        }

        private static Structure FindExternalStructure(StructureSet ss)
        {
            var external = ss.Structures.FirstOrDefault(s =>
                s != null &&
                string.Equals(s.DicomType, "EXTERNAL", StringComparison.OrdinalIgnoreCase));

            if (external != null)
                return external;

            return FindFirstExistingStructure(ss, new[] { "BODY", "Body", "External" });
        }

        private static Structure RecreateStructure(StructureSet ss, string dicomType, string id)
        {
            var existing = FindStructureById(ss, id);
            if (existing != null)
            {
                if (!ss.CanRemoveStructure(existing))
                    throw new ApplicationException("No se puede eliminar la estructura existente '" + id + "'.");

                ss.RemoveStructure(existing);
            }

            if (!ss.CanAddStructure(dicomType, id))
                throw new ApplicationException("No se puede crear la estructura '" + id + "' de tipo '" + dicomType + "'.");

            return ss.AddStructure(dicomType, id);
        }

        private static SegmentVolume Expand(Structure structure, double marginMm)
        {
            if (structure == null)
                return null;

            return structure.Margin(marginMm);
        }

        private static SegmentVolume Shrink(Structure structure, double marginMm)
        {
            if (structure == null)
                return null;

            var margins = new AxisAlignedMargins(
                StructureMarginGeometry.Inner,
                marginMm, marginMm, marginMm,
                marginMm, marginMm, marginMm);

            return structure.AsymmetricMargin(margins);
        }

        private static SegmentVolume ExpandPosteriorOnly(Structure structure, double marginPosteriorMm)
        {
            if (structure == null)
                return null;

            var margins = new AxisAlignedMargins(
                StructureMarginGeometry.Outer,
                0.0, 0.0, 0.0,
                0.0, marginPosteriorMm, 0.0);

            return structure.AsymmetricMargin(margins);
        }

        private static SegmentVolume ExpandPosteriorOnly(SegmentVolume volume, double marginPosteriorMm, StructureSet ss, string tempId)
        {
            if (volume == null)
                return null;

            var temp = RecreateStructure(ss, "CONTROL", tempId);
            temp.SegmentVolume = volume;
            var expanded = ExpandPosteriorOnly(temp, marginPosteriorMm);
            CleanupTemporaryStructure(ss, temp);
            return expanded;
        }

        private static void CleanupTemporaryStructure(StructureSet ss, Structure structure)
        {
            if (ss == null || structure == null)
                return;

            if (ss.CanRemoveStructure(structure))
                ss.RemoveStructure(structure);
        }

        private static SegmentVolume UnionStructures(IEnumerable<Structure> structures)
        {
            SegmentVolume union = null;
            foreach (var structure in structures.Where(s => s != null))
            {
                union = union == null ? structure.SegmentVolume : union.Or(structure.SegmentVolume);
            }
            return union;
        }

        private static Structure CreateExpandedOar(
            StructureSet ss,
            string targetId,
            IEnumerable<Structure> sources,
            double outerMarginMm,
            IEnumerable<SegmentVolume> ptvExclusions,
            Color color,
            List<string> notes,
            string sourceLabel,
            bool useHighResolutionWorkspace)
        {
            var validSources = sources.Where(s => s != null).ToList();
            if (!validSources.Any())
            {
                notes.Add("No se encontro " + sourceLabel + ". Se omitio " + targetId + ".");
                return null;
            }

            var target = RecreateStructureForWorkspace(ss, "CONTROL", targetId, useHighResolutionWorkspace, notes);

            var sourceUnion = UnionStructures(validSources);
            target.SegmentVolume = sourceUnion;
            target.SegmentVolume = target.Margin(outerMarginMm);

            foreach (var exclusion in ptvExclusions.Where(v => v != null))
                target.SegmentVolume = target.SegmentVolume.Sub(exclusion);

            target.Color = color;
            return target;
        }

        private static Structure CreateSpinalCordStructure(
            StructureSet ss,
            Structure spinalCord,
            Structure caudaEquina,
            double symmetricMarginMm,
            double posteriorMarginMm,
            IEnumerable<SegmentVolume> ptvExclusions,
            List<string> notes,
            bool useHighResolutionWorkspace)
        {
            string localStage = "crear estructura";

            try
            {
                var validSources = new[] { spinalCord, caudaEquina }.Where(s => s != null).ToList();
                if (!validSources.Any())
                {
                    notes.Add("No se encontro SpinalCord/CaudaEquina. Se omitio z_SpinalCord.");
                    return null;
                }

                var zCord = RecreateStructureForWorkspace(ss, "CONTROL", "z_SpinalCord", useHighResolutionWorkspace, notes);

                localStage = "union spinal cord + cauda";
                zCord.SegmentVolume = UnionStructures(validSources);

                localStage = "margin simetrico";
                zCord.SegmentVolume = zCord.Margin(symmetricMarginMm);

                localStage = "expansion posterior";
                zCord.SegmentVolume = ExpandPosteriorOnly(zCord, posteriorMarginMm);

                localStage = "recorte contra ptv exclusions";
                foreach (var exclusion in ptvExclusions.Where(v => v != null))
                    zCord.SegmentVolume = zCord.SegmentVolume.Sub(exclusion);

                localStage = "asignar color";
                zCord.Color = Color.FromRgb(128, 0, 255);

                return zCord;
            }
            catch (Exception ex)
            {
                throw new ApplicationException("CreateSpinalCordStructure -> " + localStage + ": " + ex.Message);
            }
        }

        private static Structure CreateRing(
            StructureSet ss,
            Structure basePtv,
            string ringId,
            double outerMarginMm,
            double innerMarginMm,
            Color color)
        {
            if (basePtv == null)
                return null;

            var ring = RecreateStructure(ss, "PTV", ringId);
            var outer = Expand(basePtv, outerMarginMm);
            var inner = Shrink(basePtv, innerMarginMm);
            ring.SegmentVolume = outer.Sub(inner);
            ring.Color = color;
            return ring;
        }

        private static void CropRingByFemoralHeads(Structure ring, SegmentVolume femoralHeadLPlus4, SegmentVolume femoralHeadRPlus4)
        {
            if (ring == null)
                return;

            if (femoralHeadLPlus4 != null)
                ring.SegmentVolume = ring.SegmentVolume.Sub(femoralHeadLPlus4);

            if (femoralHeadRPlus4 != null)
                ring.SegmentVolume = ring.SegmentVolume.Sub(femoralHeadRPlus4);
        }
        //Helpers para HR
        private static bool AnyHighResolution(IEnumerable<Structure> structures)
        {
            return structures != null && structures.Any(s => s != null && s.IsHighResolution);
        }

        private static Structure RecreateStructureForWorkspace(
            StructureSet ss,
            string dicomType,
            string id,
            bool useHighResolutionWorkspace,
            List<string> notes)
        {
            var structure = RecreateStructure(ss, dicomType, id);

            if (useHighResolutionWorkspace)
                EnsureHighResolution(structure, notes, id);

            return structure;
        }

        private static Structure CreateTemporaryCopy(
            StructureSet ss,
            Structure source,
            string tempId,
            bool useHighResolutionWorkspace,
            List<Structure> temporaryStructures,
            List<string> notes)
        {
            if (source == null)
                return null;

            var temp = RecreateStructure(ss, "CONTROL", tempId);
            temporaryStructures.Add(temp);

            if (useHighResolutionWorkspace)
                EnsureHighResolution(temp, notes, tempId);

            temp.SegmentVolume = source.SegmentVolume;

            if (useHighResolutionWorkspace)
                EnsureHighResolution(temp, notes, tempId);

            return temp;
        }

        private static void EnsureHighResolution(
            Structure structure,
            List<string> notes,
            string label)
        {
            if (structure == null || structure.IsHighResolution)
                return;

            if (structure.CanConvertToHighResolution())
            {
                structure.ConvertToHighResolution();
                return;
            }

            throw new ApplicationException(
                "La estructura temporal/final '" + label + "' no pudo convertirse a High Resolution.");
        }

        private static void CleanupTemporaryStructures(
            StructureSet ss,
            IEnumerable<Structure> temporaryStructures)
        {
            if (ss == null || temporaryStructures == null)
                return;

            foreach (var temp in temporaryStructures.Where(s => s != null).ToList())
            {
                CleanupTemporaryStructure(ss, temp);
            }
        }

        //helper para PTVs HR---------------------------------------------------
        private static void ApplyWorkspacePtvsToOriginals(
            Structure ptv1,
            Structure ptv2,
            Structure ptv3,
            Structure wPtv1,
            Structure wPtv2,
            Structure wPtv3,
            bool allowConvertOriginalPtvsToHighResolution,
            List<string> notes)
        {
            ApplyWorkspacePtvToOriginal(
                ptv1,
                wPtv1,
                "PTV1",
                allowConvertOriginalPtvsToHighResolution,
                notes);

            ApplyWorkspacePtvToOriginal(
                ptv2,
                wPtv2,
                "PTV2",
                allowConvertOriginalPtvsToHighResolution,
                notes);

            if (ptv3 != null && wPtv3 != null)
            {
                ApplyWorkspacePtvToOriginal(
                    ptv3,
                    wPtv3,
                    "PTV3",
                    allowConvertOriginalPtvsToHighResolution,
                    notes);
            }
        }

        private static void ApplyWorkspacePtvToOriginal(
            Structure originalPtv,
            Structure workspacePtv,
            string label,
            bool allowConvertOriginalToHighResolution,
            List<string> notes)
        {
            if (originalPtv == null || workspacePtv == null)
                return;

            string editError;
            if (!originalPtv.CanEditSegmentVolume(out editError))
            {
                notes.Add(label + ": no se pudo modificar el PTV original. " + editError);
                return;
            }

            if (workspacePtv.IsHighResolution && !originalPtv.IsHighResolution)
            {
                if (!allowConvertOriginalToHighResolution)
                {
                    notes.Add(
                        label + ": no se copio el recorte al PTV original porque el workspace esta en High Resolution " +
                        "y el PTV original no. Para evitar modificar la resolucion original, se dejo sin cambios.");
                    return;
                }

                if (!originalPtv.CanConvertToHighResolution())
                {
                    notes.Add(
                        label + ": no se pudo convertir el PTV original a High Resolution. No se copio el recorte.");
                    return;
                }

                originalPtv.ConvertToHighResolution();
                notes.Add(label + ": el PTV original fue convertido a High Resolution para copiar el recorte.");
            }

            if (!workspacePtv.IsHighResolution && originalPtv.IsHighResolution)
            {
                notes.Add(
                    label + ": no se copio el recorte porque el PTV original es High Resolution " +
                    "pero el workspace no lo es.");
                return;
            }

            originalPtv.SegmentVolume = workspacePtv.SegmentVolume;
            notes.Add(label + ": recorte copiado desde workspace al PTV original.");
        }

    }
}
