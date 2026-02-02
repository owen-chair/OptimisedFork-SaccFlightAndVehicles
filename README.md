Optimised fork of github.com/Sacchan-VRC/SaccFlightAndVehicles

This repo is an optimised version for quest2

I only wanted to use the buggy and truck so they are the only vehicles tested, others especially air vehicles might be broken in it (didn't check)

I forgot everything I did in it but general result is that it's much more efficient and doesn't clog the network or cause CPU spikes anymore

Most people probably wouldn't want to use this but it's here for reference



Optimisations include:
- Aggressive network optimisations, reducing rates from 1kbps+ to generally 300bytes/second and 0 when stationary
  - Lots of time gating, more smoothing, data packing, distance checks, unused vehicles arent updated
  - much more I forgot about
- Aggressive code CPU usage optimisations
  - lot of stuff I can't even remember
  - culled unneccessary updates and function calls, cached variables, cheaper calculations
- Fixed rubberbanding when a client is laggy, and smoothing is generally better
- Fixed network clog and CPU spikes during crashes especially with 5+ vehicles (can mush a lot of active vehicles up and it's fine now)
- Changed shaders on the dashboard display to use less heavy ones for flat green stuff instead of full lighting ones
- Also changed the main shader to a stripped down version of the standard lite
- Added time gating on a lot of toggleables like horns, etc
- Removed all dynamic lights
- Hand turret minigun is aggressively optimised; network data is packed, spool periods prevents firing spam, angles are smoothed and update rate is lower (clicking rapidly and moving the angle a lot caused unneccessarily high data usage)
- Added LOD groups to reduce tri count when theres lots of vehicles (mobiles can have 20+ cars now)
  - the LODs were lazily made but it's not super obvious
- Probably a lot more I forgot about

<img width="895" height="511" alt="image" src="https://github.com/user-attachments/assets/df21c625-5adb-45b5-8667-92f9e804419a" />
