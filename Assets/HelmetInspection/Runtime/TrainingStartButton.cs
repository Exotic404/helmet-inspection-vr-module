namespace HelmetInspection
{
    public sealed class TrainingStartButton : MechanicalTrainingButtonBase
    {
        protected override bool CanActivate => base.CanActivate && !session.IsStarted && session.TargetCount > 0;

        protected override void Activate() => session?.BeginTraining();
    }
}
