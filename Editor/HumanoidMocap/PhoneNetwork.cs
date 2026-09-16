using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace HumanoidMocap.Editor;

internal sealed record PhoneNetwork(string Name,IPAddress Address,bool HasGateway,bool Virtual)
{
    public string Label=>$"{Name} · {Address}"+(Virtual?" (virtual)":"");
    public bool LocalOnly=>IPAddress.IsLoopback(Address)||Address.GetAddressBytes()[0]==169&&Address.GetAddressBytes()[1]==254;

    public static PhoneNetwork[] Discover()
    {
        var networks=new List<PhoneNetwork>();
        foreach(var adapter in NetworkInterface.GetAllNetworkInterfaces().Where(n=>n.OperationalStatus==OperationalStatus.Up))
        {
            var properties=adapter.GetIPProperties();
            var gateway=properties.GatewayAddresses.Any(g=>g.Address.AddressFamily==AddressFamily.InterNetwork&&!g.Address.Equals(IPAddress.Any));
            var description=adapter.Name+" "+adapter.Description;
            var virtualNetwork=adapter.NetworkInterfaceType is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Loopback
                ||new[]{"virtual","vethernet","wsl","hyper-v","vmware","vpn","wireguard","tailscale","zerotier","tap-"}
                    .Any(word=>description.Contains(word,StringComparison.OrdinalIgnoreCase));
            networks.AddRange(properties.UnicastAddresses.Where(a=>a.Address.AddressFamily==AddressFamily.InterNetwork&&!IPAddress.IsLoopback(a.Address))
                .Select(a=>new PhoneNetwork(adapter.Name,a.Address,gateway,virtualNetwork)));
        }
        if(networks.Count==0)networks.Add(new("This PC only",IPAddress.Loopback,false,false));
        return Order(networks);
    }

    internal static PhoneNetwork[] Order(IEnumerable<PhoneNetwork> networks)=>networks
        .OrderBy(n=>n.LocalOnly).ThenBy(n=>n.Virtual).ThenByDescending(n=>n.HasGateway)
        .ThenBy(n=>n.Name,StringComparer.OrdinalIgnoreCase).ThenBy(n=>n.Address.ToString(),StringComparer.Ordinal).ToArray();
}
