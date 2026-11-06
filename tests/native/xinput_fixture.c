#include <windows.h>
typedef struct { WORD buttons; BYTE lt,rt; SHORT lx,ly,rx,ry; } PAD;
typedef struct { DWORD packet; PAD pad; } STATE;
static DWORD errors[4]={0,1167,0,1167};
static WORD buttons[4]={0x1000,0xffff,0x10,0xffff};
__declspec(dllexport) void WINAPI SetSlot(DWORD i,DWORD error,WORD b) { if(i<4){errors[i]=error;buttons[i]=b;} }
__declspec(dllexport) DWORD WINAPI XInputGetState(DWORD i,STATE *s) {
 if(i>=4)return 87;
 s->packet=i+10;s->pad.buttons=buttons[i];s->pad.lt=17;s->pad.rt=33;
 s->pad.lx=-100;s->pad.ly=200;s->pad.rx=-300;s->pad.ry=400;
 return errors[i];
}
