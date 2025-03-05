import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';

@Injectable({
  providedIn: 'root'
})
export class ChatDocService {
  private apiUrl = 'http://localhost:7157/api/ProcessAIRequest'; // Change this to your Azure Function URL

  constructor(private http: HttpClient) {}

  // Send text-based request
  sendTextRequest(type: 'chat', message: string): Observable<any> {
    const body = { type, message };
    return this.http.post<any>(this.apiUrl, body);
  }

  // Send file-based request using FormData
  sendFileRequest(type: 'summarize' | 'ask' | 'image', file: File): Observable<any> {
    const formData = new FormData();
    formData.append('type', type);
    formData.append('file', file);

    return this.http.post<any>(this.apiUrl, formData);
  }

  sendImageRequest(message: string): Observable<any> {
    const body = { type: 'image', message };
    return this.http.post<any>(this.apiUrl, body);
  }
}
