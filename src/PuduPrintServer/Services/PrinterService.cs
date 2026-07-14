using System.IO.Ports;
using System.Net.Sockets;

namespace PuduPrintServer.Services;

public class PrinterService
{
    /// <summary>Envía a impresora USB o Bluetooth via SerialPort (COM).</summary>
    public async Task PrintComAsync(byte[] data, string portName)
    {
        await Task.Run(() =>
        {
            using var port = new SerialPort(portName, 9600, Parity.None, 8, StopBits.One)
            {
                WriteTimeout = 5000
            };
            port.Open();
            port.BaseStream.Write(data, 0, data.Length);
            port.BaseStream.Flush();
        });
    }

    /// <summary>Envía a impresora IP via TCP (puerto 9100).</summary>
    public async Task PrintIpAsync(byte[] data, string ip, int puerto = 9100)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(ip, puerto).WaitAsync(TimeSpan.FromSeconds(5));
        using var stream = tcp.GetStream();
        await stream.WriteAsync(data).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await stream.FlushAsync();
    }

    /// <summary>Imprime ticket de prueba a una impresora local (USB/BT).</summary>
    public async Task PrintPruebaComAsync(string portName)
    {
        var bytes = EscPos.Concat(
            EscPos.Initialize(),
            EscPos.Center(), EscPos.BoldOn(),
            EscPos.Line("PUDU PRINT SERVER"),
            EscPos.BoldOff(),
            EscPos.Line("Prueba de conexion OK"),
            EscPos.Line(DateTime.Now.ToString("dd/MM/yyyy HH:mm")),
            EscPos.Feed(3), EscPos.Cut()
        );
        await PrintComAsync(bytes, portName);
    }

    /// <summary>Imprime ticket de prueba a IP.</summary>
    public async Task PrintPruebaIpAsync(string ip, int puerto = 9100)
    {
        var bytes = EscPos.Concat(
            EscPos.Initialize(),
            EscPos.Center(), EscPos.BoldOn(),
            EscPos.Line("PUDU PRINT SERVER"),
            EscPos.BoldOff(),
            EscPos.Line("Prueba de conexion IP OK"),
            EscPos.Line($"{ip}:{puerto}"),
            EscPos.Line(DateTime.Now.ToString("dd/MM/yyyy HH:mm")),
            EscPos.Feed(3), EscPos.Cut()
        );
        await PrintIpAsync(bytes, ip, puerto);
    }
}
