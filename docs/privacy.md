# RoboMouse Privacy Policy

_Last updated: 23 September 2026_

RoboMouse shares your mouse, keyboard and clipboard between computers that you own and have
paired with each other. It is designed so that your data never leaves those computers.

## What RoboMouse does with your data

- **Input and clipboard content** (mouse movement, key presses, copied text, images and files)
  is sent only to the computers you have paired, over your local network, and only while you are
  controlling that computer or have copied something to share. It is encrypted in transit.
- **Discovery broadcasts** on your local network contain the computer's name, a random RoboMouse
  machine id, its screen size, the port RoboMouse listens on and its RoboMouse identity public key,
  so other RoboMouse instances on the same network can list it.
- **Settings**, including the pairing code and the list of peers, are stored in a file on your
  computer under your user profile, next to this computer's identity key (encrypted with Windows
  DPAPI for your account). Nothing is stored anywhere else.
- **A diagnostic log** is written to the same folder to help troubleshoot connection problems. It
  contains connection events (machine names and addresses) and error messages. It never contains
  keystrokes or clipboard content. After an unexpected error, the error details are also saved
  there in `crash.txt`.
- **Export diagnostics** (Settings > About) creates a zip file on your computer, in a place you
  choose, with the logs (including the desktop service's, if installed), `crash.txt`, your settings
  with the pairing code and keys removed, the RoboMouse and Windows versions and your monitor
  layout. It is not sent anywhere; you decide whether to attach it to a bug report.

## What RoboMouse does not do

- It does not send any of your data to the developer, to Microsoft, or to any server on the
  internet. The only internet request it makes is the update check below, which carries none.
- It does not collect analytics, telemetry, crash reports or usage statistics.
- It does not use advertising or tracking of any kind.
- It does not create an account or require you to sign in.

## Update check

The versions downloaded from GitHub (not the Microsoft Store version, which the Store updates)
check for a new release once a day. The check is an ordinary HTTPS request to `api.github.com` for
the latest RoboMouse release; it carries a `User-Agent` header naming RoboMouse and its version, as
GitHub requires, and nothing else about you or your computer. As with any web request, GitHub sees
your IP address. Nothing is downloaded or installed: if there is a newer version, RoboMouse tells
you and opens the release page when you ask. Turn it off under **Settings > About > Check for
updates**.

## Network access

Apart from the update check, RoboMouse uses the network only to talk to the computers you pair it
with. The Store package declares the `internetClient` and `privateNetworkClientServer` capabilities
because Windows requires them for any local network communication; the Store version makes no
internet requests.

## Children

RoboMouse is a general-purpose utility and does not knowingly collect information from anyone.

## Changes

If this policy changes, the updated text will be published at the same address and the date at
the top will be updated.

## Contact

Questions about this policy can be raised at
[github.com/timothydodd/RoboMouse/issues](https://github.com/timothydodd/RoboMouse/issues).
