using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using HardwareCatalogExporter.Openness;

namespace HardwareCatalogExporter.WinForms
{
    internal sealed class PortalWorker
    {
        private readonly TaskCompletionSource<Control> ready =
            new TaskCompletionSource<Control>(TaskCreationOptions.RunContinuationsAsynchronously);
        private PortalSession session;

        public PortalWorker()
        {
            var thread = new Thread(() =>
            {
                try
                {
                    using (session = new PortalSession())
                    using (var dispatcher = new Control())
                    {
                        _ = dispatcher.Handle;
                        ready.SetResult(dispatcher);
                        Application.Run();
                    }
                }
                catch (Exception exception)
                {
                    ready.TrySetException(exception);
                }
            }) { IsBackground = true, Name = "TIA Portal Openness" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        public async Task<T> Run<T>(Func<PortalSession, T> operation)
        {
            var dispatcher = await ready.Task.ConfigureAwait(false);
            var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            dispatcher.BeginInvoke(new MethodInvoker(() =>
            {
                try { result.SetResult(operation(session)); }
                catch (Exception exception) { result.SetException(exception); }
            }));
            return await result.Task.ConfigureAwait(false);
        }

        public Task<bool> Stop() => Run(session =>
        {
            try { session.Disconnect(); }
            finally { Application.ExitThread(); }
            return true;
        });
    }
}
