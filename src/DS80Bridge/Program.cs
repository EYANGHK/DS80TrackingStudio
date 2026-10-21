namespace DS80Bridge;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        bool sim = args.Any(a => a.Equals("--sim", StringComparison.OrdinalIgnoreCase));
        bool device = args.Any(a => a.Equals("--device", StringComparison.OrdinalIgnoreCase));
        bool autoStart = args.Any(a => a.Equals("--autostart", StringComparison.OrdinalIgnoreCase));

        var form = new MainForm(sim);

        if (autoStart && device)
        {
            form.Shown += (_, _) => form.StartDevice();
        }
        else if (autoStart && sim)
        {
            form.Shown += (_, _) => form.StartSimulator();
        }

        Application.Run(form);
    }
}
