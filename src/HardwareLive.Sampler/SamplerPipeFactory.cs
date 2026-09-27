using System.IO.Pipes;
using System.Security.AccessControl;

namespace HardwareLive.Sampler;

public static class SamplerPipeFactory
{
    public static NamedPipeServerStream Create(string pipeName, PipeSecurity security)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentNullException.ThrowIfNull(security);

        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.Out,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.FirstPipeInstance | PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            security);
    }
}
