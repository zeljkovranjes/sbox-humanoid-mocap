using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Editor;
using Sandbox;
using HumanoidMocap.Editor.PhoneQr;

namespace HumanoidMocap.Editor;

public sealed class PhoneUploadDialog : Dialog
{
    PhoneUploadServer server;
    readonly Label status;
    readonly QrWidget qr;
    readonly LineEdit address;
    readonly Action<string> received;
    IPAddress selected;
    internal string PairingUrl=>server?.PairingUrl;
    internal Rect QrScreenRect=>qr.ScreenRect;
    public PhoneUploadDialog(Widget parent,Action<string> received):base(parent)
    {
        this.received=received;Window.WindowTitle="Receive from Phone";Window.SetWindowIcon("qr_code_2");
        Window.MinimumSize=new Vector2(440,680);
        Layout=Layout.Column();Layout.Margin=20;Layout.Spacing=12;
        SetStyles($"background-color: {Theme.WindowBackground.Hex}; color: {Theme.Text.Hex};");
        Layout.Add(new Label.Subtitle("Receive from Phone"){Alignment=TextFlag.Center});
        var subtitle=Layout.Add(new Label("Scan once · upload videos for one hour",this){Alignment=TextFlag.Center});
        subtitle.SetStyles($"color: {Theme.TextLight.Hex};");
        var networks=PhoneNetwork.Discover();selected=networks[0].Address;
        var network=Layout.Add(new ComboBox(this));
        network.ToolTip="Choose the Wi-Fi or Ethernet connection shared with your phone. Virtual adapters such as WSL are usually unreachable from phones.";
        foreach(var connection in networks)network.AddItem(connection.Label,"wifi",()=>{selected=connection.Address;Start();},selected:connection.Address.Equals(selected));
        // Fill the available width. The painter centers the square and mark on both axes.
        qr=Layout.Add(new QrWidget(this){MinimumSize=new Vector2(300,300)},1);
        address=Layout.Add(new LineEdit(this));address.ReadOnly=true;
        status=Layout.Add(new Label("",this){Alignment=TextFlag.Center});
        status.SetStyles($"color: {Theme.Green.Hex};");
        var help=Layout.Add(new Label("Connect your phone to the same router as this PC.\nUse Wi-Fi or Ethernet above, not a WSL/VPN adapter.\nIf the phone page never loads, allow s&box through\nWindows Firewall for private and public networks.\nKeep this window open to receive more videos.",this){Alignment=TextFlag.Center,WordWrap=true});
        help.SetStyles($"color: {Theme.TextLight.Hex};");
        var row=Layout.AddRow();row.Spacing=8;
        var renew=row.Add(new Button.Primary("New one-hour pairing"){Icon="refresh"},1);renew.Clicked=Start;
        var stop=row.Add(new Button("Disconnect phones","link_off"));stop.Clicked=()=>{server?.Dispose();server=null;qr.Code=null;qr.Update();status.Text="Disconnected. Existing phone links no longer work.";address.Text="";};
        Start();Window.Size=new Vector2(500,700);
    }
    void Start()
    {
        server?.Dispose();
        try
        {
            var folder=Path.Combine(Project.Current.GetAssetsPath(),"humanoid_mocap","uploads");
            server=new PhoneUploadServer(selected,folder);
            server.Received=path=>_=NotifyAsync(path);
            server.Error=message=>_=StatusAsync(message);
            server.Progress=(done,total)=>_=StatusAsync($"Receiving video · {100*done/total}%");
            server.ThemeCss=$":root{{--window:{Theme.WindowBackground.Hex};--surface:{Theme.WidgetBackground.Hex};--border:{Theme.Border.Hex};--text:{Theme.Text.Hex};--muted:{Theme.TextLight.Hex};--primary:{Theme.Blue.Hex};}}";
            qr.Code=PhoneQrPresentation.Create(server.PairingUrl);qr.Update();address.Text=server.PairingUrl;
            var connection=PhoneNetwork.Discover().FirstOrDefault(n=>n.Address.Equals(selected));
            status.Text=connection?.LocalOnly==true?"This address cannot receive uploads from a phone. Connect to Wi-Fi or Ethernet."
                :connection?.Virtual==true?"Virtual adapter selected. Choose Wi-Fi or Ethernet if your phone cannot connect."
                :"Ready · one-hour pairing. Uploads stay on this PC.";
        }
        catch(Exception e){status.Text=e.Message;}
    }
    async System.Threading.Tasks.Task NotifyAsync(string path)
    {await EditorPipeline.SwitchToMainThread();if(!this.IsValid())return;status.Text="Received "+Path.GetFileName(path);received(path);}
    async System.Threading.Tasks.Task StatusAsync(string message)
    {await EditorPipeline.SwitchToMainThread();if(this.IsValid())status.Text=message;}
    public override void OnDestroyed(){server?.Dispose();base.OnDestroyed();}
    sealed class QrWidget : Widget
    {
        public QrCode Code { get; set; }
        public QrWidget(Widget parent):base(parent){}
        protected override void OnPaint()=>PhoneQrPainter.Draw(Code,(int)Width,(int)Height);
    }
}
