using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using VMS.TPS.Common.Model.API;
using VMS.TPS.Common.Model.Types;

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
        public void Execute(ScriptContext context)
        {
            if (context == null) throw new ArgumentNullException("context");
            if (context.Patient == null) throw new ApplicationException("No hay paciente cargado.");

            AutoPlanRuntimeSettings ui = AutoPlanSettingsReader.Load();
            ExternalPlanSetup plan = FindExternalPlanById(context.Patient, ui.CourseId, ui.PlanId);

            context.Patient.BeginModifications();

            try
            {
                OptimizerResult optimizerResult = RunVmatOptimization(plan, ui);
                if (!optimizerResult.Success)
                    throw new ApplicationException("La optimización VMAT falló. Razón: " + SafeDetails(optimizerResult));

                CalculationResult doseResult = plan.CalculateDose();
                if (!doseResult.Success)
                    throw new ApplicationException("El cálculo de dosis final falló. Razón: " + SafeDetails(doseResult));

                MessageBox.Show(BuildSuccessMessage(plan, optimizerResult, doseResult), "Optimización y cálculo final", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error en optimización/cálculo final:\n\n" + ex, "FinalDoseCalculation3", MessageBoxButton.OK, MessageBoxImage.Error);
                throw;
            }
        }

        private static OptimizerResult RunVmatOptimization(ExternalPlanSetup plan, AutoPlanRuntimeSettings ui)
        {
            if (plan == null) throw new ArgumentNullException("plan");
            string mlcId = string.IsNullOrWhiteSpace(ui.VmatMlcId) ? string.Empty : ui.VmatMlcId.Trim();

            if (ui.UseIntermediateDoseDuringOptimization)
            {
                var options = new OptimizationOptionsVMAT(OptimizationIntermediateDoseOption.UseIntermediateDose, mlcId);
                return plan.OptimizeVMAT(options);
            }

            if (!string.IsNullOrWhiteSpace(mlcId))
                return plan.OptimizeVMAT(mlcId);

            return plan.OptimizeVMAT();
        }

        private static ExternalPlanSetup FindExternalPlanById(Patient patient, string courseId, string planId)
        {
            if (patient == null) throw new ArgumentNullException("patient");
            if (string.IsNullOrWhiteSpace(planId)) throw new ApplicationException("Debes definir PlanId en la interfaz.");

            IEnumerable<Course> courses = patient.Courses;
            if (!string.IsNullOrWhiteSpace(courseId))
            {
                Course course = courses.FirstOrDefault(c => string.Equals(c.Id, courseId, StringComparison.OrdinalIgnoreCase));
                if (course == null) throw new ApplicationException("No encontré el Course.Id = " + courseId);
                ExternalPlanSetup plan = course.ExternalPlanSetups.FirstOrDefault(p => string.Equals(p.Id, planId, StringComparison.OrdinalIgnoreCase));
                if (plan == null) throw new ApplicationException("No encontré el Plan.Id = " + planId + " dentro del Course.Id = " + courseId);
                return plan;
            }

            List<ExternalPlanSetup> matches = courses.SelectMany(c => c.ExternalPlanSetups).Where(p => string.Equals(p.Id, planId, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0) throw new ApplicationException("No encontré ningún ExternalPlanSetup con Id = " + planId);
            if (matches.Count > 1) throw new ApplicationException("Encontré más de un plan con Id = " + planId + ". Define también CourseId para evitar ambigüedad.");
            return matches[0];
        }

        private static string SafeDetails(object result)
        {
            return result == null ? "(resultado nulo)" : result.ToString();
        }

        private static string BuildSuccessMessage(ExternalPlanSetup plan, OptimizerResult optimizerResult, CalculationResult doseResult)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Optimización VMAT completada exitosamente.");
            sb.AppendLine("Dosis final calculada exitosamente.");
            sb.AppendLine();
            sb.AppendLine("Plan: " + plan.Id);
            sb.AppendLine();
            sb.AppendLine("OptimizerResult:");
            sb.AppendLine(SafeDetails(optimizerResult));
            sb.AppendLine();
            sb.AppendLine("CalculationResult:");
            sb.AppendLine(SafeDetails(doseResult));
            return sb.ToString();
        }
    }
}
