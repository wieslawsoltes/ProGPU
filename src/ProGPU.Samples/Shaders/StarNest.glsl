// Algorithm: Accumulate the original folded-space Star Nest volumetric field.
// Time complexity: O(V*F) per fragment for V=12 volume steps and F=15 fold iterations.
// Space complexity: O(1) local storage.
// Star Nest by Kali
#define iterations 15
#define formuparam 0.53

#define volsteps 12
#define stepsize 0.1

#define zoom   0.800
#define tile   0.850
#define speed  0.010

#define brightness 0.0015
#define darkmatter 0.300
#define distfading 0.730
#define saturation 0.850

void mainImage( out vec4 fragColor, in vec2 fragCoord )
{
	//get coords and direction
	vec2 uv=fragCoord.xy/iResolution.xy-.5;
	uv.y*=iResolution.y/iResolution.x;
	vec3 dir=vec3(uv*zoom,1.);
	float time=iTime*speed+.25;

	//mouse rotation
	float s=sin(time*0.3), c=cos(time*0.3);
	if (iMouse.z > 0.0) {
		float mouseNorm = iMouse.x / iResolution.x;
		s = sin(mouseNorm * 3.14);
		c = cos(mouseNorm * 3.14);
	}

	vec3 from=vec3(1.,.5,0.5);
	from+=vec3(time*2.,time,-2.);

	//volumetric rendering
	float s_val=0.1,fade=1.;
	vec3 v=vec3(0.);
	for (int r=0; r<volsteps; r++) {
		vec3 p=from+s_val*dir*s_val;
		p = abs(vec3(tile)-mod(p,vec3(tile*2.))); // modulation
		float pa,a=pa=0.;
		for (int i=0; i<iterations; i++) {
			p=abs(p)/dot(p,p)-vec3(formuparam); // the magic formula
			a+=abs(length(p)-pa); // absolute sum of average change
			pa=length(p);
		}
		float dm=max(0.,darkmatter-a*a*.001); //dark matter
		a*=a*a; // add contrast
		if (r>6) fade*=1.-dm; // dark matter signup with distance
		v+=fade;
		v+=vec3(s_val,s_val*s_val,s_val*s_val*s_val)*a*brightness*fade; // coloring based on distance
		fade*=distfading; // distance fading
		s_val+=stepsize;
	}
	v=mix(vec3(length(v)),v,saturation); //color adjust
	fragColor = vec4(v*.01,1.);
}
