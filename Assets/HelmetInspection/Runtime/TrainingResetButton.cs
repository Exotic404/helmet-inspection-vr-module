namespace HelmetInspection
{
    public sealed class TrainingResetButton : MechanicalTrainingButtonBase
    {
        protected override void Activate() => session?.ResetTraining();
    }
}
