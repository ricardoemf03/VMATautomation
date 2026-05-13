using System;
using System.Linq;
using System.Text;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Reflection;
using VMS.TPS.Common.Model.API;
using VMS.TPS.Common.Model.Types;

// TODO: Replace the following version attributes by creating AssemblyInfo.cs. You can do this in the properties of the Visual Studio project.
[assembly: AssemblyVersion("1.0.0.1")]
[assembly: AssemblyFileVersion("1.0.0.1")]
[assembly: AssemblyInformationalVersion("1.0")]

[assembly: ESAPIScript(IsWriteable = true)]

namespace VMS.TPS
{
    public class Script
    {
        public Script() { }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Execute(ScriptContext context /*, Window window, ScriptEnvironment environment*/)
        {
            if (context == null)
                throw new ArgumentNullException("context");

            if (context.Patient == null)
                throw new ApplicationException("No hay paciente cargado.");

            //if (context.StructureSet == null)
            //    throw new ApplicationException("No hay StructureSet activo.");

            //ExternalPlanSetup plan = context.PlanSetup as ExternalPlanSetup;
            //if (plan == null)
            //    throw new ApplicationException("No hay ExternalPlanSetup activo.");

            // -----------------------------------------------------------------
            // PLACEHOLDERS para futura interfaz gráfica / ViewModel.
            // Si TargetCourseId queda "", buscará el plan en todos los cursos.
            // Si hay más de un plan con el mismo Id, pedirá definir el CourseId.
            // -----------------------------------------------------------------
            const string TargetCourseId = "C1";      // Cambiar por el Course.Id real, o dejar "".
            const string TargetPlanId = "plan1";     // Cambiar por el Plan.Id real.

            ExternalPlanSetup plan = FindExternalPlanById(
                context.Patient,
                TargetCourseId,
                TargetPlanId);

            context.Patient.BeginModifications();

            try
            {
                // -----------------------------------------------------------------
                // PLACEHOLDERS para futura interfaz gráfica / ViewModel.
                // 
                // Si ptv3Id viene null o "", BuildFromUiInputs lo ignora automáticamente.
                // -----------------------------------------------------------------
                string ptv1Id = "PTV_45Gy";
                double? ptv1DoseGy = 45.0;
                string ptv1ModelId = "PTV_50Gy";

                string ptv2Id = "PTV_55Gy";
                double? ptv2DoseGy = 55.0;
                string ptv2ModelId = "PTV_50Gy";

                string ptv3Id = "PTV_57.5Gy"; // Ejemplo: null o "" cuando no existe tercer PTV.
                double? ptv3DoseGy = 57.5;
                string ptv3ModelId = "PTV_50Gy";

                DvhEstimationRequest request = DvhEstimationRequestFactory.BuildFromUiInputs(
                    ptv1Id, ptv1DoseGy, ptv1ModelId,
                    ptv2Id, ptv2DoseGy, ptv2ModelId,
                    ptv3Id, ptv3DoseGy, ptv3ModelId);

                DvhEstimationExecutionResult execution = RapidPlanDvhModule.Run(context, plan, request);

                MessageBox.Show(
                    execution.UserMessage,
                    execution.Success ? "DVH Estimation OK" : "DVH Estimation",
                    MessageBoxButton.OK,
                    execution.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Error:\n" + ex,
                    "Excepción",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                throw;
            }
        }

        //Metodo para encontrar el plan
        private static ExternalPlanSetup FindExternalPlanById(
            Patient patient,
            string courseId,
            string planId)
        {
            if (patient == null)
                throw new ArgumentNullException("patient");

            if (string.IsNullOrWhiteSpace(planId))
                throw new ApplicationException("Debes definir TargetPlanId.");

            IEnumerable<Course> courses = patient.Courses;

            if (!string.IsNullOrWhiteSpace(courseId))
            {
                Course course = courses.FirstOrDefault(c =>
                    string.Equals(c.Id, courseId, StringComparison.OrdinalIgnoreCase));

                if (course == null)
                    throw new ApplicationException("No encontré el Course.Id = " + courseId);

                ExternalPlanSetup plan = course.ExternalPlanSetups.FirstOrDefault(p =>
                    string.Equals(p.Id, planId, StringComparison.OrdinalIgnoreCase));

                if (plan == null)
                {
                    throw new ApplicationException(
                        "No encontré el Plan.Id = " + planId +
                        " dentro del Course.Id = " + courseId);
                }

                return plan;
            }

            List<ExternalPlanSetup> matches = courses
                .SelectMany(c => c.ExternalPlanSetups)
                .Where(p => string.Equals(p.Id, planId, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matches.Count == 0)
                throw new ApplicationException("No encontré ningún ExternalPlanSetup con Id = " + planId);

            if (matches.Count > 1)
            {
                throw new ApplicationException(
                    "Encontré más de un plan con Id = " + planId +
                    ". Define también TargetCourseId para evitar ambigüedad.");
            }

            return matches[0];
        }

    }

    // =====================================================================
    //  MÓDULO PRINCIPAL
    // =====================================================================
    public static class RapidPlanDvhModule
    {
        public static DvhEstimationExecutionResult Run(
            ScriptContext context,
            ExternalPlanSetup plan,
            DvhEstimationRequest request)
        {
            if (context == null)
                throw new ArgumentNullException("context");
            if (plan == null)
                throw new ArgumentNullException("plan");
            if (request == null)
                throw new ArgumentNullException("request");

            DvhEstimationExecutionResult execution = new DvhEstimationExecutionResult();
            StringBuilder log = new StringBuilder();

            DVHEstimationModelSummary model = SelectModel(context, request.DesiredModelName, request.RequirePublishedAndTrained, log);
            if (model == null)
            {
                execution.Success = false;
                execution.UserMessage = log.ToString();
                return execution;
            }

            string modelIdForCalculation = model.Name;

            HashSet<string> planStructureIds = new HashSet<string>(
                plan.StructureSet.Structures.Select(s => s.Id),
                StringComparer.OrdinalIgnoreCase);

            HashSet<string> modelStructureIds = LoadModelStructureIds(context, model, log);

            Dictionary<string, DoseValue> targetDoseLevels = new Dictionary<string, DoseValue>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string   > structureMatches = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            AddConfiguredTargets(request, planStructureIds, modelStructureIds, targetDoseLevels, structureMatches, log);
            AddConfiguredExactMatches(request, planStructureIds, modelStructureIds, structureMatches, log);

            if (request.UseAutomaticOarAliasMatching)
                AddAutomaticOarMatches(request, planStructureIds, modelStructureIds, structureMatches, log);

            if (request.ShowDebugSummary)
                ShowDebug(model, modelIdForCalculation, targetDoseLevels, structureMatches, log.ToString());

            if (targetDoseLevels.Count == 0)
            {
                execution.Success = false;
                execution.UserMessage =
                    "No se agregó ningún target válido para CalculateDVHEstimates.\n\n" +
                    log.ToString();
                return execution;
            }

            try
            {
                CalculationResult result = plan.CalculateDVHEstimates(
                    modelIdForCalculation,
                    targetDoseLevels,
                    structureMatches);

                string details = GetCalcResultDetails(result);

                //if (result.Success && request.NormalTissueObjective != null && request.NormalTissueObjective.Enabled)
                //{
                //    ApplyNormalTissueObjective(plan, request.NormalTissueObjective, log);
                //}
                if (result.Success)
                {
                    RemoveMeanDoseObjectivesForStructure(plan, "z_RVR", log);

                    ApplyManualMeanDoseObjectives(plan, request.ManualMeanDoseObjectives, log);

                    if (request.NormalTissueObjective != null && request.NormalTissueObjective.Enabled)
                    {
                        ApplyNormalTissueObjective(plan, request.NormalTissueObjective, log);
                    }
                }

                execution.Success = result.Success;
                execution.UserMessage = BuildFinalMessage(result.Success, details, log.ToString());
                execution.CalculationDetails = details;
                return execution;
            }
            catch (Exception ex)
            {
                execution.Success = false;
                execution.UserMessage =
                    "Excepción en CalculateDVHEstimates:\n" + ex.Message + "\n\n" + log.ToString();
                return execution;
            }
        }

        private static DVHEstimationModelSummary SelectModel(
            ScriptContext context,
            string desiredModelName,
            bool requirePublishedAndTrained,
            StringBuilder log)
        {
            IEnumerable<DVHEstimationModelSummary> models = context.Calculation.GetDvhEstimationModelSummaries();

            IEnumerable<DVHEstimationModelSummary> query = models
                .Where(m => string.Equals(m.Name, desiredModelName, StringComparison.OrdinalIgnoreCase));

            if (requirePublishedAndTrained)
                query = query.Where(m => m.IsPublished && m.IsTrained);

            DVHEstimationModelSummary model = query
                .OrderByDescending(m => SafeToInt(Convert.ToString(m.Revision)))
                .FirstOrDefault();

            if (model == null)
            {
                log.AppendLine("No encontré un modelo DVH válido con Name = " + desiredModelName);
                log.AppendLine();
                log.AppendLine("Modelos visibles:");

                foreach (DVHEstimationModelSummary item in models.OrderBy(x => x.Name))
                {
                    log.AppendLine(
                        "Name=" + NullSafe(item.Name) +
                        " | Revision=" + NullSafe(item.Revision) +
                        " | Published=" + item.IsPublished +
                        " | Trained=" + item.IsTrained +
                        " | UID=" + NullSafe(item.ModelUID));
                }

                return null;
            }

            log.AppendLine("Modelo seleccionado: " + model.Name + " | Revision=" + NullSafe(model.Revision));
            log.AppendLine("ModelUID: " + NullSafe(model.ModelUID));
            log.AppendLine();
            return model;
        }

        private static HashSet<string> LoadModelStructureIds(
            ScriptContext context,
            DVHEstimationModelSummary model,
            StringBuilder log)
        {
            HashSet<string> ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                IEnumerable<DVHEstimationModelStructure> modelStructures =
                    context.Calculation.GetDvhEstimationModelStructures(model.ModelUID);

                foreach (DVHEstimationModelStructure s in modelStructures)
                {
                    if (!string.IsNullOrWhiteSpace(s.Id))
                        ids.Add(s.Id);
                }

                log.AppendLine("Estructuras leídas del modelo: " + ids.Count);
                log.AppendLine();
            }
            catch (Exception ex)
            {
                log.AppendLine("Advertencia: no pude leer las estructuras del modelo con GetDvhEstimationModelStructures.");
                log.AppendLine("Se continuará sin validación previa de IDs del modelo.");
                log.AppendLine("Detalle: " + ex.Message);
                log.AppendLine();
            }

            return ids;
        }

        private static void AddConfiguredTargets(
            DvhEstimationRequest request,
            HashSet<string> planStructureIds,
            HashSet<string> modelStructureIds,
            Dictionary<string, DoseValue> targetDoseLevels,
            Dictionary<string, string> structureMatches,
            StringBuilder log)
        {
            if (request.Targets == null)
                return;

            foreach (TargetDoseMapping target in request.Targets)
            {
                if (target == null)
                    continue;

                if (string.IsNullOrWhiteSpace(target.PlanStructureId))
                    continue;

                if (!planStructureIds.Contains(target.PlanStructureId))
                {
                    log.AppendLine("Target omitido (no existe en plan): " + target.PlanStructureId);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(target.ModelStructureId))
                {
                    log.AppendLine("Target omitido (sin ModelStructureId): " + target.PlanStructureId);
                    continue;
                }

                if (modelStructureIds.Count > 0 && !modelStructureIds.Contains(target.ModelStructureId))
                {
                    log.AppendLine(
                        "Target omitido (no existe en modelo): " +
                        target.PlanStructureId + " -> " + target.ModelStructureId);
                    continue;
                }

                double? doseGy = target.DoseGy;
                if (!doseGy.HasValue && request.AllowDoseInferenceFromStructureId)
                    doseGy = TryInferDoseFromText(target.PlanStructureId);

                if (!doseGy.HasValue && request.AllowDoseInferenceFromStructureId)
                    doseGy = TryInferDoseFromText(target.ModelStructureId);

                if (!doseGy.HasValue)
                {
                    log.AppendLine(
                        "Target omitido (sin dosis definida): " + target.PlanStructureId +
                        " -> " + target.ModelStructureId);
                    continue;
                }

                targetDoseLevels[target.PlanStructureId] = new DoseValue(doseGy.Value, DoseValue.DoseUnit.Gy);
                structureMatches[target.PlanStructureId] = target.ModelStructureId;

                log.AppendLine(
                    "Target agregado: " + target.PlanStructureId +
                    " -> " + target.ModelStructureId +
                    " | Dose = " + doseGy.Value.ToString("0.###") + " Gy");
            }

            log.AppendLine();
        }

        private static void AddConfiguredExactMatches(
            DvhEstimationRequest request,
            HashSet<string> planStructureIds,
            HashSet<string> modelStructureIds,
            Dictionary<string, string> structureMatches,
            StringBuilder log)
        {
            if (request.FixedStructureMatches == null)
                return;

            foreach (StructureMatchItem item in request.FixedStructureMatches)
            {
                if (item == null)
                    continue;

                if (string.IsNullOrWhiteSpace(item.PlanStructureId) || string.IsNullOrWhiteSpace(item.ModelStructureId))
                    continue;

                if (!planStructureIds.Contains(item.PlanStructureId))
                {
                    log.AppendLine("Match exacto omitido (no existe en plan): " + item.PlanStructureId);
                    continue;
                }

                if (modelStructureIds.Count > 0 && !modelStructureIds.Contains(item.ModelStructureId))
                {
                    log.AppendLine(
                        "Match exacto omitido (no existe en modelo): " +
                        item.PlanStructureId + " -> " + item.ModelStructureId);
                    continue;
                }

                structureMatches[item.PlanStructureId] = item.ModelStructureId;
                log.AppendLine("Match exacto agregado: " + item.PlanStructureId + " -> " + item.ModelStructureId);
            }

            log.AppendLine();
        }

        private static void AddAutomaticOarMatches(
            DvhEstimationRequest request,
            HashSet<string> planStructureIds,
            HashSet<string> modelStructureIds,
            Dictionary<string, string> structureMatches,
            StringBuilder log)
        {
            IEnumerable<OarAliasRule> rules = request.OarAliasRules ?? BuildDefaultOarAliasRules();

            foreach (OarAliasRule rule in rules)
            {
                if (rule == null)
                    continue;

                if (string.IsNullOrWhiteSpace(rule.ModelStructureId))
                    continue;

                if (modelStructureIds.Count > 0 && !modelStructureIds.Contains(rule.ModelStructureId))
                {
                    log.AppendLine(
                        "Alias OAR omitido (ModelStructureId no existe en modelo): " +
                        rule.RuleName + " -> " + rule.ModelStructureId);
                    continue;
                }

                string matchedPlanId = FindFirstAliasMatch(planStructureIds, rule.Aliases);
                if (string.IsNullOrWhiteSpace(matchedPlanId))
                {
                    log.AppendLine("OAR no encontrado para alias: " + rule.RuleName);
                    continue;
                }

                structureMatches[matchedPlanId] = rule.ModelStructureId;
                log.AppendLine("OAR agregado por alias: " + matchedPlanId + " -> " + rule.ModelStructureId);
            }

            log.AppendLine();
        }

        private static string FindFirstAliasMatch(HashSet<string> planStructureIds, IEnumerable<string> aliases)
        {
            if (aliases == null)
                return null;

            foreach (string alias in aliases)
            {
                if (string.IsNullOrWhiteSpace(alias))
                    continue;

                foreach (string planId in planStructureIds)
                {
                    if (string.Equals(planId, alias, StringComparison.OrdinalIgnoreCase))
                        return planId;
                }
            }

            return null;
        }

        private static void ApplyManualMeanDoseObjectives(
            ExternalPlanSetup plan,
            IEnumerable<ManualMeanDoseObjectiveSettings> objectives,
            StringBuilder log)
        {
            if (plan == null)
                throw new ArgumentNullException("plan");

            if (objectives == null)
                return;

            if (plan.OptimizationSetup == null)
            {
                log.AppendLine("Advertencia: el plan no tiene OptimizationSetup disponible para aplicar objetivos Mean Dose manuales.");
                return;
            }

            foreach (ManualMeanDoseObjectiveSettings item in objectives)
            {
                if (item == null || !item.Enabled)
                    continue;

                if (string.IsNullOrWhiteSpace(item.StructureId))
                    continue;

                Structure structure = plan.StructureSet.Structures.FirstOrDefault(s =>
                    string.Equals(s.Id, item.StructureId, StringComparison.OrdinalIgnoreCase));

                if (structure == null)
                {
                    log.AppendLine("Objetivo Mean Dose omitido: no existe la estructura " + item.StructureId);
                    continue;
                }

                plan.OptimizationSetup.AddMeanDoseObjective(
                    structure,
                    new DoseValue(item.DoseGy, DoseValue.DoseUnit.Gy),
                    item.Priority);

                log.AppendLine(
                    "Objetivo Mean Dose agregado: " + structure.Id +
                    " <= " + item.DoseGy.ToString("0.###") + " Gy" +
                    " | Priority=" + item.Priority.ToString("0.###"));
            }

            log.AppendLine();
        }

        private static void RemoveMeanDoseObjectivesForStructure(
            ExternalPlanSetup plan,
            string structureId,
            StringBuilder log)
        {
            if (plan == null)
                throw new ArgumentNullException("plan");

            if (string.IsNullOrWhiteSpace(structureId))
                return;

            if (plan.OptimizationSetup == null)
            {
                log.AppendLine("Advertencia: el plan no tiene OptimizationSetup disponible para borrar objetivos.");
                return;
            }

            List<OptimizationMeanDoseObjective> objectivesToRemove =
                plan.OptimizationSetup.Objectives
                    .OfType<OptimizationMeanDoseObjective>()
                    .Where(o => string.Equals(o.StructureId, structureId, StringComparison.OrdinalIgnoreCase))
                    .ToList();

            foreach (OptimizationMeanDoseObjective objective in objectivesToRemove)
            {
                plan.OptimizationSetup.RemoveObjective(objective);
            }

            log.AppendLine(
                "Objetivos Mean Dose eliminados para " + structureId + ": " +
                objectivesToRemove.Count);

            log.AppendLine();
        }

        private static void ApplyNormalTissueObjective(
            ExternalPlanSetup plan,
            NormalTissueObjectiveSettings nto,
            StringBuilder log)
        {
            if (plan.OptimizationSetup == null)
            {
                log.AppendLine("Advertencia: el plan no tiene OptimizationSetup disponible para aplicar NTO.");
                return;
            }

            plan.OptimizationSetup.AddNormalTissueObjective(
                nto.Priority,
                nto.DistanceFromTargetBorderInMM,
                nto.StartDosePercentage,
                nto.EndDosePercentage,
                nto.FallOff);

            log.AppendLine(
                "NTO aplicado/actualizado: Priority=" + nto.Priority.ToString("0.###") +
                ", Distance(mm)=" + nto.DistanceFromTargetBorderInMM.ToString("0.###") +
                ", StartDose(%)=" + nto.StartDosePercentage.ToString("0.###") +
                ", EndDose(%)=" + nto.EndDosePercentage.ToString("0.###") +
                ", FallOff=" + nto.FallOff.ToString("0.###"));
            log.AppendLine();
        }

        private static IEnumerable<OarAliasRule> BuildDefaultOarAliasRules()
        {
            return new List<OarAliasRule>
            {
                new OarAliasRule("Bladder",        "Bladder",     new[] { "Bladder", "BLADDER" }),
                new OarAliasRule("BowelBag",       "Bowel_Bag",   new[] { "Bowel_Bag", "BowelBag", "BOWEL_BAG" }),
                new OarAliasRule("Rectum",         "Rectum",      new[] { "Rectum", "RECTUM" }),
                new OarAliasRule("ColonSigmoid",   "Rectum",      new[] { "Colon_Sigmoid", "Sigmoid", "ColonSigmoid" }),
                new OarAliasRule("SpinalCord",     "SpinalCord",  new[] { "SpinalCord", "Spinal_Cord", "Cord", "SPINALCORD" }),
                new OarAliasRule("CaudaEquina",    "SpinalCord",  new[] { "CaudaEquina", "Cauda_Equina", "CAUDAEQUINA" }),
                new OarAliasRule("KidneyL",        "Kidney",      new[] { "Kidney_L", "Kidney L", "Left Kidney", "LT Kidney" }),
                new OarAliasRule("KidneyR",        "Kidney",      new[] { "Kidney_R", "Kidney R", "Right Kidney", "RT Kidney" }),
                new OarAliasRule("FemoralHeadL",   "Femur",       new[] { "Femoral_Head_L", "FemoralHead_L", "FemHead_L", "Left Femoral Head" }),
                new OarAliasRule("FemoralHeadR",   "Femur",       new[] { "Femoral_Head_R", "FemoralHead_R", "FemHead_R", "Right Femoral Head" }),
                new OarAliasRule("Liver",          "Liver",       new[] { "Liver", "LIVER" })
            };
        }

        private static void ShowDebug(
            DVHEstimationModelSummary model,
            string modelId,
            Dictionary<string, DoseValue> targetDoseLevels,
            Dictionary<string, string> structureMatches,
            string logText)
        {
            StringBuilder sb = new StringBuilder();

            sb.AppendLine("=== Modelo DVH seleccionado ===");
            sb.AppendLine("Name: " + NullSafe(model.Name));
            sb.AppendLine("Revision: " + NullSafe(model.Revision));
            sb.AppendLine("ModelUID: " + NullSafe(model.ModelUID));
            sb.AppendLine("IsPublished: " + model.IsPublished);
            sb.AppendLine("IsTrained: " + model.IsTrained);
            sb.AppendLine("TreatmentSite: " + NullSafe(model.TreatmentSite));
            sb.AppendLine("ModelParticleType: " + model.ModelParticleType);
            sb.AppendLine();
            sb.AppendLine("modelId usado en CalculateDVHEstimates:");
            sb.AppendLine(modelId);
            sb.AppendLine();

            sb.AppendLine("=== TargetDoseLevels ===");
            foreach (KeyValuePair<string, DoseValue> kv in targetDoseLevels)
                sb.AppendLine(kv.Key + " -> " + kv.Value.Dose.ToString("0.###") + " " + kv.Value.UnitAsString);

            sb.AppendLine();
            sb.AppendLine("=== StructureMatches ===");
            foreach (KeyValuePair<string, string> kv in structureMatches)
                sb.AppendLine(kv.Key + " -> " + kv.Value);

            sb.AppendLine();
            sb.AppendLine("=== Log interno ===");
            sb.AppendLine(logText);

            MessageBox.Show(sb.ToString(), "RapidPlan Debug", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private static string BuildFinalMessage(bool success, string details, string logText)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(success ? "DVH Estimation completada correctamente." : "DVH Estimation terminó con errores.");
            sb.AppendLine();
            sb.AppendLine(details);

            if (!string.IsNullOrWhiteSpace(logText))
            {
                sb.AppendLine();
                sb.AppendLine("===== Resumen de preparación =====");
                sb.AppendLine(logText);
            }

            return sb.ToString();
        }

        private static string GetCalcResultDetails(CalculationResult result)
        {
            try
            {
                List<string> parts = new List<string>();
                parts.Add("Success = " + result.Success);
                parts.Add("ResultType = " + result.GetType().FullName);

                Type t = result.GetType();

                AppendPropertyValue(parts, result, t, "Message");
                AppendPropertyValue(parts, result, t, "Errors");
                AppendPropertyValue(parts, result, t, "Warnings");
                AppendEnumerableProperty(parts, result, t, "ErrorMessages");
                AppendEnumerableProperty(parts, result, t, "WarningMessages");

                return string.Join("\n", parts.ToArray());
            }
            catch (Exception ex)
            {
                return "No se pudieron leer detalles del CalculationResult: " + ex.Message;
            }
        }

        private static void AppendPropertyValue(List<string> parts, object obj, Type type, string propertyName)
        {
            var property = type.GetProperty(propertyName);
            if (property == null)
                return;

            object value = property.GetValue(obj, null);
            if (value == null)
                return;

            string text = value.ToString();
            if (string.IsNullOrWhiteSpace(text))
                return;

            parts.Add(propertyName + ": " + text);
        }

        private static void AppendEnumerableProperty(List<string> parts, object obj, Type type, string propertyName)
        {
            var property = type.GetProperty(propertyName);
            if (property == null)
                return;

            IEnumerable enumerable = property.GetValue(obj, null) as IEnumerable;
            if (enumerable == null)
                return;

            List<string> list = new List<string>();
            foreach (object item in enumerable)
            {
                if (item != null)
                    list.Add(item.ToString());
            }

            if (list.Count > 0)
                parts.Add(propertyName + ":\n- " + string.Join("\n- ", list.ToArray()));
        }

        private static double? TryInferDoseFromText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            // Busca números como 45, 50, 55, 57.5, etc. seguidos opcionalmente por Gy.
            string normalized = text.Replace(',', '.');
            List<char> chars = new List<char>();

            for (int i = 0; i < normalized.Length; i++)
            {
                char c = normalized[i];
                if (char.IsDigit(c) || c == '.')
                    chars.Add(c);
                else
                    chars.Add(' ');
            }

            string[] tokens = new string(chars.ToArray())
                .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (string token in tokens.OrderByDescending(s => s.Length))
            {
                double value;
                if (double.TryParse(token, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out value))
                {
                    if (value > 0.0 && value < 1000.0)
                        return value;
                }
            }

            return null;
        }

        private static int SafeToInt(string text)
        {
            int value;
            return int.TryParse(text, out value) ? value : -1;
        }

        private static string NullSafe(object o)
        {
            return o == null ? string.Empty : o.ToString();
        }


    }

    // =====================================================================
    //  DATOS DE ENTRADA / SALIDA
    // =====================================================================
    public class DvhEstimationExecutionResult
    {
        public bool Success { get; set; }
        public string UserMessage { get; set; }
        public string CalculationDetails { get; set; }
    }

    public class DvhEstimationRequest
    {
        public string DesiredModelName { get; set; }
        public bool RequirePublishedAndTrained { get; set; }
        public bool ShowDebugSummary { get; set; }
        public bool UseAutomaticOarAliasMatching { get; set; }
        public bool AllowDoseInferenceFromStructureId { get; set; }
        public List<TargetDoseMapping> Targets { get; set; }
        public List<StructureMatchItem> FixedStructureMatches { get; set; }
        public List<OarAliasRule> OarAliasRules { get; set; }
        public NormalTissueObjectiveSettings NormalTissueObjective { get; set; }

        public List<ManualMeanDoseObjectiveSettings> ManualMeanDoseObjectives { get; set; }
    }

    public class TargetDoseMapping
    {
        public TargetDoseMapping() { }

        public TargetDoseMapping(string planStructureId, double? doseGy, string modelStructureId)
        {
            PlanStructureId = planStructureId;
            DoseGy = doseGy;
            ModelStructureId = modelStructureId;
        }

        public string PlanStructureId { get; set; }
        public double? DoseGy { get; set; }
        public string ModelStructureId { get; set; }
    }

    public class StructureMatchItem
    {
        public StructureMatchItem() { }

        public StructureMatchItem(string planStructureId, string modelStructureId)
        {
            PlanStructureId = planStructureId;
            ModelStructureId = modelStructureId;
        }

        public string PlanStructureId { get; set; }
        public string ModelStructureId { get; set; }
    }

    public class OarAliasRule
    {
        public OarAliasRule() { }

        public OarAliasRule(string ruleName, string modelStructureId, IEnumerable<string> aliases)
        {
            RuleName = ruleName;
            ModelStructureId = modelStructureId;
            Aliases = aliases == null ? new List<string>() : new List<string>(aliases);
        }

        public string RuleName { get; set; }
        public string ModelStructureId { get; set; }
        public List<string> Aliases { get; set; }
    }

    public class NormalTissueObjectiveSettings
    {
        public bool Enabled { get; set; }
        public double Priority { get; set; }
        public double DistanceFromTargetBorderInMM { get; set; }
        public double StartDosePercentage { get; set; }
        public double EndDosePercentage { get; set; }
        public double FallOff { get; set; }
    }

    public class ManualMeanDoseObjectiveSettings
    {
        public bool Enabled { get; set; }
        public string StructureId { get; set; }
        public double DoseGy { get; set; }
        public double Priority { get; set; }
    }

    // =====================================================================
    //  FACTORY DE EJEMPLO
    // =====================================================================
    public static class DvhEstimationRequestFactory
    {
        public static DvhEstimationRequest BuildDefaultRequest()
        {
            return new DvhEstimationRequest
            {
                DesiredModelName = "PLV-INCAN",
                RequirePublishedAndTrained = true,
                ShowDebugSummary = true,
                UseAutomaticOarAliasMatching = true,
                AllowDoseInferenceFromStructureId = false,

                // ==============================================================
                // PLACEHOLDERS para futura interfaz gráfica:
                // aquí poner el PTV seleccionado por el usuario, su dosis,
                // y el nombre de la estructura target equivalente dentro del modelo.
                //
                // no hay lógica fija de "3 PTVs". se puede dejar 1, 2, 3 o más.
                // Si ptv3 viene como null o como "", simplemente NO se agrega.
                // ==============================================================
                Targets = new List<TargetDoseMapping>
                {
                    new TargetDoseMapping("PTV_45Gy",   45.0, "PTV_50Gy"),
                    new TargetDoseMapping("PTV_55Gy",   55.0, "PTV_50Gy"),
                    new TargetDoseMapping("PTV_57.5Gy", 57.5, "PTV_50Gy"),

                    // Aros / z_PTV_* con dosis explícita.
                    new TargetDoseMapping("z_PTV_A45Gy",   45.0, "z_PTV_50GGy"),
                    new TargetDoseMapping("z_PTV_A55Gy",   55.0, "z_PTV_50GGy"),
                    new TargetDoseMapping("z_PTV_A57.5Gy", 57.5, "z_PTV_50GGy")
                },

                // ==============================================================
                // Estructuras exactas que no dependen de alias y que quieres que
                // se mantengan tal como están, especialmente las z_*.
                // ==============================================================
                FixedStructureMatches = new List<StructureMatchItem>
                {
                    new StructureMatchItem("z_Bladder",    "Bladder"),
                    new StructureMatchItem("z_Kidneys",    "Kidney"),
                    new StructureMatchItem("z_Rectum",     "Rectum"),
                    new StructureMatchItem("z_SpinalCord", "SpinalCord"),
                    new StructureMatchItem("z_RVR",        "NS_Control")
                },

                // Si se quiere personalizar alias desde fuera, se llena esto.
                // Si se deja null, el módulo usa BuildDefaultOarAliasRules().
                OarAliasRules = null,

                NormalTissueObjective = new NormalTissueObjectiveSettings
                {
                    Enabled = true,
                    Priority = 190.0,
                    DistanceFromTargetBorderInMM = 2.0,
                    StartDosePercentage = 102.0,
                    EndDosePercentage = 48.0,
                    FallOff = 0.28
                }
            };
        }

        // -----------------------------------------------------------------
        // EJEMPLO para cuando la interfaz mande ptv1, ptv2, ptv3.
        // Si ptv3 = null o ptv3 = "", no se agrega y el script sigue normal.
        // Este helper está pensado para reemplazar luego al BuildDefaultRequest().
        // -----------------------------------------------------------------
        public static DvhEstimationRequest BuildFromUiInputs(
            string ptv1Id, double? ptv1DoseGy, string ptv1ModelId,
            string ptv2Id, double? ptv2DoseGy, string ptv2ModelId,
            string ptv3Id, double? ptv3DoseGy, string ptv3ModelId)
        {
            List<TargetDoseMapping> targets = new List<TargetDoseMapping>();

            AddTargetAndRingIfValid(targets, ptv1Id, ptv1DoseGy, ptv1ModelId);
            AddTargetAndRingIfValid(targets, ptv2Id, ptv2DoseGy, ptv2ModelId);
            AddTargetAndRingIfValid(targets, ptv3Id, ptv3DoseGy, ptv3ModelId);

            // Aquí puedes seguir agregando los z_PTV_A* si también vienen de la UI,
            // o dejarlos fijos si sus nombres default no cambian.

            return new DvhEstimationRequest
            {
                DesiredModelName = "PLV-INCAN",
                RequirePublishedAndTrained = true,
                ShowDebugSummary = true,
                UseAutomaticOarAliasMatching = true,
                AllowDoseInferenceFromStructureId = false,
                Targets = targets,
                FixedStructureMatches = new List<StructureMatchItem>
                {
                    new StructureMatchItem("z_Bladder",    "Bladder"),
                    new StructureMatchItem("z_Kidneys",    "Kidney"),
                    new StructureMatchItem("z_Rectum",     "Rectum"),
                    new StructureMatchItem("z_SpinalCord", "SpinalCord"),
                    new StructureMatchItem("z_RVR",        "NS_Control")
                },
                OarAliasRules = null,

                //objetivos manuales para Bone_Marrow
                ManualMeanDoseObjectives = new List<ManualMeanDoseObjectiveSettings>
                {
                    new ManualMeanDoseObjectiveSettings
                    {
                        Enabled = true,
                        StructureId = "Bone_Marrow",
                        DoseGy = 26.0,
                        Priority = 80.0
                    },

                    new ManualMeanDoseObjectiveSettings
                    {
                        Enabled = true,
                        StructureId = "z_RVR",
                        DoseGy = 25.0,
                        Priority = 80.0
                    }
                },

                NormalTissueObjective = new NormalTissueObjectiveSettings
                {
                    Enabled = true,
                    Priority = 190.0,
                    DistanceFromTargetBorderInMM = 2.0,
                    StartDosePercentage = 102.0,
                    EndDosePercentage = 48.0,
                    FallOff = 0.28
                }
            };
        }

        private static void AddTargetAndRingIfValid(
            List<TargetDoseMapping> targets,
            string planStructureId,
            double? doseGy,
            string modelStructureId)
        {
            if (targets == null)
                return;

            if (string.IsNullOrWhiteSpace(planStructureId))
                return;

            if (!doseGy.HasValue)
                return;

            if (string.IsNullOrWhiteSpace(modelStructureId))
                return;

            string cleanPtvId = planStructureId.Trim();
            string ringId = BuildRingIdFromPtvId(cleanPtvId);

            // PTV principal
            targets.Add(new TargetDoseMapping(cleanPtvId, doseGy.Value, modelStructureId));

            // Aro asociado con la misma dosis
            if (!string.IsNullOrWhiteSpace(ringId))
            {
                targets.Add(new TargetDoseMapping(ringId, doseGy.Value, "z_PTV_50GGy"));
            }
        }

        private static string BuildRingIdFromPtvId(string ptvId)
        {
            if (string.IsNullOrWhiteSpace(ptvId))
                return null;

            return "z_" + ptvId.Trim() + "_A";
        }

        
    }
}

