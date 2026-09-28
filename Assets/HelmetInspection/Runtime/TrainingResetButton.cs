namespace HelmetInspection
{
    public sealed class TrainingResetButton : MechanicalTrainingButtonBase
    {
        protected override bool UsesRestartFeedback => true;

        protected override void Activate() => session?.ResetTraining();
    }
}
