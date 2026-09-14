namespace HelmetInspection
{
    public sealed class TrainingStartButton : MechanicalTrainingButtonBase
    {
        protected override void Activate() => session?.BeginTraining();
    }
}
